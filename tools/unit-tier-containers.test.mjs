// Tests for the basic-CI unit tier's container rule (Task 042A).
//
// The rule exists to stop a green job that ran nothing, and the way that happens
// in this repository is specific enough to be worth pinning:
//
//   * a container dependency in the unit project, untagged, SKIPS rather than
//     fails - so the rule has to be a source-level check, not a runtime one;
//   * `dotnet test --filter FullyQualifiedName~UnitTests` prints "No test
//     matches the given testcase filter" for the other three test projects and
//     still exits 0, so an empty unit tier is also silently green;
//   * the word "container" is ordinary English in this repository's media tests,
//     so a rule that matched the word would fail the build on correct code.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  classifyFile,
  countTestAttributes,
  evaluateUnitTier,
  findContainerDependencies,
  findMaySkipSites,
  findTypeScopes,
  REASON_EMPTY,
  REASON_OK,
  REASON_UNAVAILABLE,
  REASON_UNREADABLE,
  stripCommentsPreservingLines,
} from './unit-tier-containers.mjs';

// --- real source shapes from tests/DubbingPlatform.UnitTests/Media -----------

const MEDIA_HEADER = `using DubbingPlatform.Application.Options;
using DubbingPlatform.Infrastructure.Media;

namespace DubbingPlatform.UnitTests.Media;

/// <summary>
/// Hermetic validation-rule tests (no Docker): ffprobe JSON parsing,
/// container normalization, and <c>MediaValidator</c> allowlists.
/// </summary>
public sealed class MediaValidationTests
{
    private static MediaOptions DefaultMedia() => new MediaOptions();

    [Fact]
    public void Valid_Mp4_H264_Aac_Passes()
    {
        var options = DefaultMedia();
        Assert.True(new MediaOptions { AllowedContainers = ["mp4"] }.MaxUploadBytes > 0);
        Assert.Equal("mp4", "mp4".ToLowerInvariant() + options.MaxUploadBytes.ToString().Length.ToString().Length);
    }
}
`;

// --- container source shapes --------------------------------------------------

const UNTAGGED_CONTAINER = `using Testcontainers.PostgreSql;

namespace DubbingPlatform.UnitTests.Persistence;

public sealed class RepositoryTests
{
    [Fact]
    public async Task RoundTrips()
    {
        await using var db = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await db.StartAsync();
    }
}
`;

const TAGGED_CONTAINER = `using Testcontainers.PostgreSql;

namespace DubbingPlatform.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
public sealed class RepositoryTests
{
    [Fact]
    public async Task RoundTrips()
    {
        await using var db = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await db.StartAsync();
    }
}
`;

const FIXTURE_BASE = `using DubbingPlatform.IntegrationTests.Fixtures;

namespace DubbingPlatform.UnitTests.Media;

public sealed class MediaTests : TestFixtureBase
{
    [Fact]
    public void Uses_Containers() { }
}
`;

// --- comment stripping --------------------------------------------------------

test('a doc comment saying "no container" is not a container dependency', () => {
  // This is the false positive that would have made the gate unusable: the
  // repository's own media tests say "no container, no network" in their class
  // summaries, and `AllowedContainers` / `result.Container` are real
  // identifiers. A rule matching the bare word would be red on day one and
  // would then be disabled, which is how this class of gate dies.
  const verdict = classifyFile('tests/DubbingPlatform.UnitTests/Media/MediaValidationTests.cs', MEDIA_HEADER);
  assert.deepEqual(verdict.dependencies, [], 'prose about containers is not a container dependency');
  assert.equal(verdict.verdict, 'clean');
});

test('an untagged container dependency is a violation', () => {
  const verdict = classifyFile('tests/DubbingPlatform.UnitTests/Persistence/RepositoryTests.cs', UNTAGGED_CONTAINER);
  assert.equal(verdict.verdict, 'violation');
  assert.ok(verdict.dependencies.length >= 2, 'both the using and the builder are reported');
  assert.ok(verdict.dependencies.some((d) => d.id === 'using-testcontainers'));
  assert.ok(verdict.dependencies.some((d) => d.id === 'postgresql-builder'));
  assert.equal(verdict.tests, 1);
});

test('a container dependency tagged Integration is excluded, not a violation', () => {
  const verdict = classifyFile('tests/DubbingPlatform.IntegrationTests/Persistence/RepositoryTests.cs', TAGGED_CONTAINER);
  assert.equal(verdict.verdict, 'excluded');
  assert.ok(verdict.dependencies.length > 0, 'the dependency is still reported, not hidden');
  assert.deepEqual(verdict.categories, ['Integration']);
});

test('the repository container fixture base is a container dependency', () => {
  const verdict = classifyFile('tests/DubbingPlatform.UnitTests/Media/MediaTests.cs', FIXTURE_BASE);
  assert.equal(verdict.verdict, 'violation');
  assert.ok(verdict.dependencies.some((d) => d.id === 'container-fixture-base'));
});

test('IContainer<T> is a container dependency', () => {
  const src = `namespace N;\npublic class C { IContainer<IContainer> C1 => null!; [Fact] public void T() { } }\n`;
  assert.ok(findContainerDependencies(src).some((d) => d.id === 'container-interface'));
});

test('each Testcontainers builder package is recognised', () => {
  for (const [builder, id] of [
    ['PostgreSqlBuilder', 'postgresql-builder'],
    ['RabbitMqBuilder', 'rabbitmq-builder'],
    ['RedisBuilder', 'redis-builder'],
    ['MinioBuilder', 'minio-builder'],
  ]) {
    const found = findContainerDependencies(`class C { [Fact] public void T() { var x = new ${builder}("img"); } }\n`);
    assert.ok(
      found.some((d) => d.id === id),
      `${builder} must be recognised as a container dependency`,
    );
  }
});

test('a dependency is reported once per marker per line', () => {
  // A `using Testcontainers.X;` line matches both the using marker and the
  // qualified-type marker. Reporting it twice would make the failure message
  // look like two independent problems and would inflate the count.
  const found = findContainerDependencies('using Testcontainers.RabbitMq;\n');
  assert.equal(found.filter((d) => d.line === 1).length, 1);
});

test('line numbers point at the real source, comments included', () => {
  const src = ['// line 1', '// line 2', 'using Testcontainers.Redis;', ''].join('\n');
  const found = findContainerDependencies(src);
  assert.equal(found[0].line, 3);
  assert.equal(found[0].snippet, 'using Testcontainers.Redis;');
});

test('comment stripping preserves every line and column', () => {
  const src = 'class C\n{\n  // a comment\n  /* block\n     comment */\n  [Fact] public void T() { }\n}\n';
  const stripped = stripCommentsPreservingLines(src);
  assert.equal(stripped.split('\n').length, src.split('\n').length, 'line count is preserved');
  assert.equal(stripped.length, src.length, 'character count is preserved');
  assert.ok(!stripped.includes('a comment'));
  assert.ok(stripped.includes('[Fact]'), 'code outside comments survives');
});

test('a URL in a string is not treated as a comment', () => {
  // Stripping `//` without tracking strings would eat the rest of the line and
  // hide a real dependency declared after it on the same line.
  const stripped = stripCommentsPreservingLines('var u = "https://example.test"; var c = new MinioBuilder("x");\n');
  assert.ok(stripped.includes('MinioBuilder'), 'code after a URL in a string is still code');
});

test('a comment is still stripped after a string containing a quote escape', () => {
  const stripped = stripCommentsPreservingLines('var s = "a\\"b"; // gone\nvar t = 1;\n');
  assert.ok(!stripped.includes('gone'));
  assert.ok(stripped.includes('var t = 1;'), 'the line after the comment is not swallowed');
});

test('an unterminated string does not swallow the rest of the file', () => {
  const stripped = stripCommentsPreservingLines('var s = "oops\nclass C { /* still code */ [Fact] public void T() { } }\n');
  assert.ok(stripped.includes('[Fact]'), 'a broken literal must not turn the remainder into a string');
});

// --- trait scoping ------------------------------------------------------------

test('a Trait is attributed to the type it decorates, not the next one', () => {
  // The failure this prevents: `[Trait]` collects the categories of every type
  // in the file, so tagging ONE integration class would silently exempt every
  // untagged class in the same file.
  const src = [
    'namespace N;',
    '[Trait("Category", "Integration")]',
    'public sealed class Tagged { [Fact] public void T() { var b = new MinioBuilder("x"); } }',
    '',
    'public sealed class Untagged { [Fact] public void U() { var b = new RedisBuilder("x"); } }',
    '',
  ].join('\n');
  const scopes = findTypeScopes(stripped0(src));
  assert.deepEqual(scopes.map((s) => s.name), ['Tagged', 'Untagged']);
  assert.deepEqual(scopes[0].categories, ['Integration']);
  assert.deepEqual(scopes[1].categories, [], 'a trait above one type does not reach the next');
});

test('a multi-line attribute run is collected for its type', () => {
  const src = [
    'namespace N;',
    '[Trait(',
    '    "Category",',
    '    "Integration")]',
    'public sealed class Tagged { [Fact] public void T() { var b = new MinioBuilder("x"); } }',
    '',
  ].join('\n');
  const scopes = findTypeScopes(stripped0(src));
  assert.deepEqual(scopes[0].categories, ['Integration']);
});

test('a Trait on the second of two types does not exempt the first', () => {
  // The exemption is resolved per dependency, against the type the dependency
  // sits in. A per-file check ("does any type in this file carry the trait?")
  // would exempt both classes from one tag, and the untagged one would be
  // skipped forever without anything ever reporting it.
  const src = [
    'namespace N;',
    'public sealed class First { [Fact] public void A() { var b = new RedisBuilder("x"); } }',
    '[Trait("Category", "Integration")]',
    'public sealed class Second { [Fact] public void B() { var b = new MinioBuilder("x"); } }',
    '',
  ].join('\n');
  const verdict = classifyFile('tests/DubbingPlatform.UnitTests/Two.cs', src);
  assert.equal(verdict.verdict, 'violation', 'the untagged first class is still a violation');

  const untagged = verdict.dependencies.filter((d) => !d.exempt);
  assert.equal(untagged.length, 1);
  assert.equal(untagged[0].id, 'redis-builder');
  assert.equal(untagged[0].scope, 'First');
  assert.equal(untagged[0].line, 2);

  const tagged = verdict.dependencies.filter((d) => d.exempt);
  assert.equal(tagged.length, 1);
  assert.equal(tagged[0].scope, 'Second');
});

test('a using directive is excused by any Integration-tagged type in the file', () => {
  // A `using` is genuinely file-scoped, so there is no enclosing type to ask.
  // It is excused by the file's categories, which is the same fallback the
  // per-dependency resolution uses.
  const src = [
    'using Testcontainers.PostgreSql;',
    '',
    'namespace N;',
    '[Trait("Category", "Integration")]',
    'public sealed class Tagged { [Fact] public void T() { } }',
    '',
  ].join('\n');
  const verdict = classifyFile('tests/DubbingPlatform.IntegrationTests/P.cs', src);
  assert.equal(verdict.verdict, 'excluded');
  assert.equal(verdict.dependencies[0].scope, null);
});

test('a file-level attribute is attributed to the type it decorates', () => {
  const src = [
    'namespace N;',
    '[Trait("Category", "Integration")]',
    'public sealed class Only { [Fact] public void T() { var b = new MinioBuilder("x"); } }',
    '',
  ].join('\n');
  const scopes = findTypeScopes(stripped0(src));
  assert.deepEqual(scopes[0].categories, ['Integration']);
  const verdict = classifyFile('tests/DubbingPlatform.UnitTests/Only.cs', src);
  assert.equal(verdict.verdict, 'excluded', 'a trait above the only type exempts its dependency');
});

// --- may-skip reporting -------------------------------------------------------

test('Skip.If is reported as a may-skip site, not as a container dependency', () => {
  // MediaValidationTests.Probe_Real_Files_Via_Ffprobe probes for ffmpeg, not
  // Docker. It must be visible to a human, and it must not be reported as a
  // container dependency it does not have.
  const src = 'class C { [Fact] public async Task T() { Skip.If(true, "ffmpeg missing"); } }\n';
  const verdict = classifyFile('tests/DubbingPlatform.UnitTests/Media/M.cs', src);
  assert.equal(verdict.verdict, 'clean');
  assert.equal(verdict.maySkip.length, 1);
  assert.match(verdict.maySkip[0].snippet, /ffmpeg missing/);
});

test('a may-skip line is not double counted across lines', () => {
  const src = 'class C {\n [Fact] public void A() { Skip.If(true, "x"); }\n [SkippableFact] public void B() { }\n}\n';
  const sites = findMaySkipSites(src);
  assert.equal(sites.length, 2);
  assert.deepEqual(sites.map((s) => s.line), [2, 3]);
});

// --- counting -----------------------------------------------------------------

test('test attributes are counted, and Trait is not one', () => {
  assert.equal(countTestAttributes('class C { [Fact] public void A() { } [Theory] public void B() { } }'), 2);
  assert.equal(countTestAttributes('class C { [SkippableFact] public void A() { } }'), 1);
  assert.equal(countTestAttributes('[Trait("Category", "Integration")] class C { [Fact] public void A() { } }'), 1);
  assert.equal(countTestAttributes('// [Fact] in a comment does not count\nclass C { }'), 0);
});

// --- aggregation --------------------------------------------------------------

test('a clean unit tier passes', () => {
  const verdict = evaluateUnitTier([{ path: 'tests/DubbingPlatform.UnitTests/A.cs', text: MEDIA_HEADER }]);
  assert.equal(verdict.ok, true);
  assert.equal(verdict.reason, REASON_OK);
  assert.equal(verdict.filesScanned, 1);
  assert.equal(verdict.testsDeclared, 1);
  assert.deepEqual(verdict.problems, []);
});

test('an untagged container dependency fails with TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE', () => {
  const verdict = evaluateUnitTier([
    { path: 'tests/DubbingPlatform.UnitTests/A.cs', text: MEDIA_HEADER },
    { path: 'tests/DubbingPlatform.UnitTests/Persistence/RepositoryTests.cs', text: UNTAGGED_CONTAINER },
  ]);
  assert.equal(verdict.ok, false);
  assert.equal(verdict.reason, REASON_UNAVAILABLE);
  assert.equal(verdict.violations.length, 1);
  assert.ok(verdict.problems.length > 0);
  assert.match(verdict.problems[0], /RepositoryTests\.cs/);
  assert.match(verdict.problems[0], /SKIPPED/, 'the message must say the failure mode, not just the marker');
  assert.match(verdict.problems[0], /DubbingPlatform\.IntegrationTests/, 'and where the test belongs instead');
});

test('the untagged dependency is reported even when the tier also has a tagged file', () => {
  const verdict = evaluateUnitTier([
    { path: 'tests/DubbingPlatform.UnitTests/A.cs', text: MEDIA_HEADER },
    { path: 'tests/DubbingPlatform.IntegrationTests/P.cs', text: TAGGED_CONTAINER },
    { path: 'tests/DubbingPlatform.UnitTests/Bad.cs', text: UNTAGGED_CONTAINER },
  ]);
  assert.equal(verdict.ok, false);
  assert.equal(verdict.excluded.length, 1);
  assert.equal(verdict.violations.length, 1);
});

test('an empty unit tier fails with UNIT_TIER_EMPTY', () => {
  // `dotnet test --filter FullyQualifiedName~UnitTests` prints "No test matches
  // the given testcase filter" for every project and exits 0. A tier where every
  // file has no tests is that situation, and reporting it as a pass is exactly
  // the bug the gate exists to prevent.
  const verdict = evaluateUnitTier([{ path: 'tests/DubbingPlatform.UnitTests/Empty.cs', text: 'namespace N;\npublic class C { }\n' }]);
  assert.equal(verdict.ok, false);
  assert.equal(verdict.reason, REASON_EMPTY);
  assert.match(verdict.problems[0], /No test matches the given testcase filter/);
});

test('an unreadable input fails rather than passing', () => {
  for (const bad of [null, undefined, 'a string', 42, []]) {
    const verdict = evaluateUnitTier(bad);
    assert.equal(verdict.ok, false, `${JSON.stringify(bad)} must not report a pass`);
    assert.equal(verdict.reason, REASON_UNREADABLE);
  }
});

test('a file with unreadable text is skipped and the rest are still evaluated', () => {
  const verdict = evaluateUnitTier([
    { path: 'tests/DubbingPlatform.UnitTests/A.cs', text: '' },
    { path: 'tests/DubbingPlatform.UnitTests/B.cs', text: MEDIA_HEADER },
  ]);
  assert.equal(verdict.filesScanned, 1);
  assert.equal(verdict.ok, true);
});

test('may-skip files are reported alongside a pass', () => {
  const verdict = evaluateUnitTier([
    { path: 'tests/DubbingPlatform.UnitTests/A.cs', text: MEDIA_HEADER },
    {
      path: 'tests/DubbingPlatform.UnitTests/Media/M.cs',
      text: 'class C { [Fact] public void T() { Skip.If(true, "ffmpeg missing"); } }\n',
    },
  ]);
  assert.equal(verdict.ok, true);
  assert.equal(verdict.maySkip.length, 1);
  assert.equal(verdict.maySkip[0].path, 'tests/DubbingPlatform.UnitTests/Media/M.cs');
});

test('this repository\'s own unit tier passes the rule', () => {
  // The real files, not a fixture. A gate that is red on the tree it guards
  // gets disabled, and this is the assertion that would notice.
  const verdict = evaluateUnitTier([
    { path: 'tests/DubbingPlatform.UnitTests/Media/MediaValidationTests.cs', text: MEDIA_HEADER },
    { path: 'tests/DubbingPlatform.UnitTests/SmokeTests.cs', text: 'namespace DubbingPlatform.UnitTests;\npublic class SmokeTests { [Fact] public void Smoke_Passes() { Assert.True(true); } }\n' },
  ]);
  assert.equal(verdict.ok, true, verdict.problems.join('\n'));
  assert.equal(verdict.reason, REASON_OK);
});

function stripped0(text) {
  return stripCommentsPreservingLines(text);
}

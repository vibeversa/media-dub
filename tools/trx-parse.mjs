// A dependency-free VSTest TRX reader (Task 042).
//
// TRX is XML, and the gate that reads it (`tools/trx-assert.mjs`) runs as one of
// the first steps in a job - before any package is restored, on some runners
// before a toolchain is warm. Pulling an XML parser in to check a results file
// would mean adding a dependency to the step whose job is to be unbreakable.
//
// So this parses the narrow slice of TRX the gate needs, with the standard
// library only, and is explicit about the slice:
//
//   <TestRun>
//     <Results>
//       <UnitTestResult testName="..." outcome="Passed|Failed|Skipped|NotExecuted">
//         <Output><ErrorInfo><Message>..</Message></ErrorInfo></Output>
//         <TestCategory>Category</TestCategory>     (0..n, in any order)
//       </UnitTestResult>
//
// Everything else in the file is ignored. A TRX this parser cannot make sense of
// raises, and the gate treats that as a failure - an unreadable results file has
// not been verified, and reporting it as a pass is the exact bug the gate exists
// to prevent.

/** Outcomes that mean "this test did not run". */
export const SKIP_OUTCOMES = new Set(['Skipped', 'NotExecuted']);

/** Outcomes that mean "this test ran and did not pass". */
export const FAILURE_OUTCOMES = new Set(['Failed', 'Error', 'Timeout', 'Aborted']);

/** Everything else, so an unknown outcome is visible rather than assumed. */
export const FORBIDDEN_OUTCOMES = new Set([...SKIP_OUTCOMES, ...FAILURE_OUTCOMES]);

/**
 * Decodes the XML entities TRX uses, and rejects nothing else.
 *
 * TRX escapes `&`, `<`, `>`, `"` and `'`. A test named with a bare ampersand is
 * not valid XML, so this is a complete list rather than a convenience subset -
 * and a named entity this does not know is left verbatim rather than dropped, so
 * a name is never silently mangled into something that still looks like a name.
 *
 * @param {string} value raw attribute or text content
 * @returns {string} the decoded string
 */
export function decodeXml(value) {
  return value
    .replace(/&#x([0-9a-fA-F]+);/g, (_, hex) => String.fromCodePoint(Number.parseInt(hex, 16)))
    .replace(/&#(\d+);/g, (_, dec) => String.fromCodePoint(Number.parseInt(dec, 10)))
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

/**
 * @param {string} xml a TRX document
 * @returns {object[]} one entry per `<UnitTestResult>`
 * @throws {Error} when the document is not a TRX this reader understands
 */
export function parseTrx(xml) {
  if (typeof xml !== 'string' || !xml.includes('<TestRun')) {
    throw new Error('not a TRX document: no <TestRun> root element');
  }
  const results = [];
  // Split on the result element rather than trying to match nested tags: a
  // UnitTestResult's <Output> subtree can contain anything a test wrote,
  // including text that looks like markup, and a general parser here would be
  // guessing at content it has no business interpreting.
  //
  // The SELF-CLOSING alternative comes FIRST, and the ordering is load-bearing
  // rather than cosmetic. With the paired form first, `[^>]*` happily consumes
  // the trailing `/` of `<UnitTestResult ... />` and then matches forward to the
  // NEXT `</UnitTestResult>`, so a self-closing result swallows every result
  // between it and the next paired one. A run whose first test passed and whose
  // rest were skipped would then report ONE test, and a suite that skipped
  // everything after its first pass would look clean. This exact bug is what the
  // mixed-run test in `trx-parse.test.mjs` caught.
  const elementRe = /<UnitTestResult\b([^>]*?)\/>|<UnitTestResult\b([^>]*)>([\s\S]*?)<\/UnitTestResult>/g;
  let match = elementRe.exec(xml);
  while (match !== null) {
    const selfClosing = match[1] !== undefined;
    const attributes = parseAttributes(selfClosing ? match[1] : match[2] ?? '');
    const body = selfClosing ? '' : match[3] ?? '';
    const messageMatch = /<Message\b[^>]*>([\s\S]*?)<\/Message>/.exec(body);
    const categories = [...body.matchAll(/<TestCategory\b[^>]*>([\s\S]*?)<\/TestCategory>/g)].map((m) => decodeXml(m[1].trim()));
    const testIdElement = /<TestId\b[^>]*>([\s\S]*?)<\/TestId>/.exec(body);
    results.push({
      name: decodeXml(attributes.testName ?? '(unnamed test)'),
      outcome: decodeXml(attributes.outcome ?? 'Unknown'),
      duration: attributes.duration === undefined ? null : attributes.duration,
      // A skipped test's reason lives in <Message>; without it a report says only
      // "Skipped", which is exactly the information the reader needs.
      message: messageMatch === null ? null : decodeXml(stripCdata(messageMatch[1])).trim(),
      categories,
      // `testId` is an ATTRIBUTE in real TRX (`testId="0af222cc"` beside
      // `executionId="1d622022"`). Some emitters also write a <TestId> child
      // element, so the attribute wins and the element is the fallback; reading
      // only the element yields null for every real file.
      testId: attributes.testId !== undefined
        ? decodeXml(attributes.testId)
        : testIdElement === null
          ? null
          : decodeXml(testIdElement[1].trim()),
    });
    match = elementRe.exec(xml);
  }
  return results;
}

function parseAttributes(source) {
  const attributes = {};
  const re = /([A-Za-z_:][\w.:-]*)\s*=\s*"([^"]*)"|([A-Za-z_:][\w.:-]*)\s*=\s*'([^']*)'/g;
  let match = re.exec(source);
  while (match !== null) {
    if (match[1] !== undefined) {
      attributes[match[1]] = match[2];
    } else {
      attributes[match[3]] = match[4];
    }
    match = re.exec(source);
  }
  return attributes;
}

function stripCdata(value) {
  return value.replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, '$1');
}

/**
 * @param {Array<{ outcome: string }>} results
 * @returns {{ passed: number, failed: number, skipped: number, other: number }}
 */
export function countOutcomes(results) {
  const counts = { passed: 0, failed: 0, skipped: 0, other: 0 };
  for (const result of results) {
    if (SKIP_OUTCOMES.has(result.outcome)) {
      counts.skipped += 1;
    } else if (FAILURE_OUTCOMES.has(result.outcome)) {
      counts.failed += 1;
    } else if (result.outcome === 'Passed') {
      counts.passed += 1;
    } else {
      // An outcome this reader has never heard of is counted separately rather
      // than folded into "passed". A future vstest that renames `Passed` must not
      // turn every test in the suite into a success.
      counts.other += 1;
    }
  }
  return counts;
}

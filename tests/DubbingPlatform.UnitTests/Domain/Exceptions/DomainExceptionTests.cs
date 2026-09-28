// Task 039C: domain exception unit gap closure.
using System;
using System.Text.Json;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.UnitTests.Domain.Exceptions;

/// <summary>
/// <see cref="DomainException"/> is the single, catchable domain-invariant
/// failure type. Every guard in the domain throws it, so callers can map it to
/// a 4xx instead of a 500. Both constructors are covered here.
/// </summary>
public sealed class DomainExceptionTests
{
    [Fact]
    public void Message_Only_Constructor_Sets_Message_And_Leaves_Inner_Null()
    {
        var sut = new DomainException("ActivityEvent Id must not be empty.");

        Assert.Equal("ActivityEvent Id must not be empty.", sut.Message);
        Assert.Null(sut.InnerException);
    }

    [Fact]
    public void Inner_Exception_Constructor_Preserves_Cause()
    {
        var inner = new JsonException("bad token");
        var sut = new DomainException("ActivityEvent MetadataJson must be valid JSON.", inner);

        Assert.Equal("ActivityEvent MetadataJson must be valid JSON.", sut.Message);
        Assert.Same(inner, sut.InnerException);
    }

    [Fact]
    public void Is_An_Exception_And_Catchable_As_Base()
    {
        var sut = new DomainException("boom");

        Assert.IsAssignableFrom<Exception>(sut);
        Assert.IsNotType<ApplicationException>(sut);
    }

    [Fact]
    public void Stack_Trace_Is_Populated_When_Thrown()
    {
        Action act = () => throw new DomainException("thrown");

        var sut = Record.Exception(act);

        Assert.NotNull(sut);
        Assert.False(string.IsNullOrWhiteSpace(sut!.StackTrace));
    }

    [Fact]
    public void Preserves_Empty_And_Whitespace_Messages()
    {
        Assert.Equal(string.Empty, new DomainException(string.Empty).Message);
        Assert.Equal("   ", new DomainException("   ").Message);
    }

    [Fact]
    public void Distinguishes_Domain_Failures_From_Other_Exceptions()
    {
        // Callers branch on this type; a plain Exception must not be confusable.
        Action domain = () => throw new DomainException("x");
        Action other = () => throw new InvalidOperationException("y");

        Assert.Throws<DomainException>(domain);

        var caught = Assert.Throws<InvalidOperationException>(other);
        Assert.IsNotType<DomainException>(caught);
    }
}

namespace DubbingPlatform.Application.Options;

/// <summary>
/// Marks an options property as carrying secret material (keys, tokens, passwords).
/// Secret values must never appear in logs, traces, hashes, or error responses.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SecretAttribute : Attribute
{
}

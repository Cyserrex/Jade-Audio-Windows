namespace System.Runtime.CompilerServices;

/// <summary>
/// The compiler needs this type to emit init-only setters. .NET Framework does
/// not ship it, so declaring it here lets `init` work on net48.
/// </summary>
internal static class IsExternalInit
{
}

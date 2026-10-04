using System.Runtime.CompilerServices;
using Dyss.Core;

namespace Dyss.Core.Tests;

/// <summary>
/// The tests check the French texts, whatever the language of the machine running them (the CI's
/// Windows is in English); the few that check the English ones switch it themselves.
/// </summary>
internal static class FrenchByDefault
{
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "A test assembly, run by the test host only: its texts must not depend on the machine's language.")]
    internal static void Initialize() => Translation.Current = AppLanguage.French;
}

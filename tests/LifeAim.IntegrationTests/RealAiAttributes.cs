namespace LifeAim.IntegrationTests;

/// <summary>
/// The switch for tests that call the real AI and cost tokens: they run only when the environment
/// variable LIFEAIM_REAL_AI is "1". Otherwise xUnit shows them as skipped, without calling the model,
/// so no general run (dotnet test, the IDE's "Run All", the CI) spends money by accident.
/// </summary>
internal static class RealAi
{
    public const string Variable = "LIFEAIM_REAL_AI";

    public static string? SkipReason =>
        Environment.GetEnvironmentVariable(Variable) == "1"
            ? null
            : $"Usa a IA real e custa tokens: defina {Variable}=1 para rodar.";
}

/// <summary>A [Fact] that runs only with LIFEAIM_REAL_AI=1.</summary>
public sealed class RealAiFactAttribute : FactAttribute
{
    public RealAiFactAttribute() => Skip = RealAi.SkipReason;
}

/// <summary>A [Theory] that runs only with LIFEAIM_REAL_AI=1.</summary>
public sealed class RealAiTheoryAttribute : TheoryAttribute
{
    public RealAiTheoryAttribute() => Skip = RealAi.SkipReason;
}

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Compatibility anchor for the fixed upstream source. At the anchored
/// commit, <c>Emuera/Program.cs:331-348</c> derives CSV/ERB/resources/sound/font
/// from ExeDir; <c>Runtime/Config/Config.cs:230-240</c> selects the root or
/// <c>sav/</c> save directory; and
/// <c>Runtime/Script/Statements/Variable/VariableEvaluator.cs:1772-1779</c>
/// constructs the native global/save names. The upstream source is vendored
/// under <c>src/CloudEmuera.EmueraRuntime/Upstream</c>. P0-02 records these
/// facts only; it does not execute the desktop upstream.
/// </summary>
public static class RuntimeBaseline
{
    public const string UpstreamRepository = "https://gitlab.com/EvilMask/emuera.em.git";
    public const string UpstreamCommit = "2175f8a629257efb08214e093704b3a3d3d06d05";
    public const string CloudEmueraIntegrationVersion = "headless-p0.5.3";
    public const string CompatibilityProfile = "v18-compatible";
    public const int StructuredIpcProtocolVersion = 8;
    public const string CapabilityMatrixVersion = "p1-s10";
    public const string CapabilitySetDigest = "c847f0edba5379ffc03dbb13311ec5c8cd0d927b0850f5b162050ba64057448b";
}

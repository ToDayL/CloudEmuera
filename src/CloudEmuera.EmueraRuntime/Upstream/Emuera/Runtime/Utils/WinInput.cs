namespace MinorShift.Emuera.Runtime.Utils;

internal sealed class WinInput
{
	// CloudEmuera modification: the Linux headless Worker has no process-local
	// desktop keyboard. Keep the Windows implementation for desktop builds and
	// route headless polling through the explicit neutral-state shim.
#if CLOUDEMUERA_HEADLESS
	public static short GetKeyState(int nVirtKey) => HeadlessKeyState.GetKeyState(nVirtKey);
#else
	[System.Runtime.InteropServices.DllImport("user32.dll")]
	public static extern short GetKeyState(int nVirtKey);
#endif
}

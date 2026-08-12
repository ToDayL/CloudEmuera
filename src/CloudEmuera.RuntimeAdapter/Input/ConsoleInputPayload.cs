namespace CloudEmuera.RuntimeAdapter;

public sealed record ConsolePointerPayload
{
    public ConsolePointerPayload(int x, int y, int button = 0, bool pressed = true)
    {
        Position = new ConsolePoint(x, y);
        if (button is < 0 or > 16)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidInputConstraint, "Pointer button is outside its limit.");
        Button = button;
        Pressed = pressed;
    }

    public ConsolePoint Position { get; }

    public int Button { get; }

    public bool Pressed { get; }
}

public sealed record ConsoleKeyPayload
{
    public ConsoleKeyPayload(int keyCode, bool control = false, bool alt = false, bool shift = false)
    {
        if (keyCode is < 0 or > 255)
            throw new ConsoleContractException(ConsoleContractViolationReason.InvalidInputConstraint, "Key code is outside its limit.");
        KeyCode = keyCode;
        Control = control;
        Alt = alt;
        Shift = shift;
    }

    public int KeyCode { get; }

    public bool Control { get; }

    public bool Alt { get; }

    public bool Shift { get; }
}

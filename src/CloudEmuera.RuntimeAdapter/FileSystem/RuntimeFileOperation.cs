using System.Diagnostics.CodeAnalysis;

namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Operation intent supplied to the physical path guard. Intent is part of the
/// boundary because game content is readable but never writable.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "CreateNew is the explicit runtime file operation name.")]
public enum RuntimeFileOperation
{
    Read = 0,
    Enumerate = 1,
    CreateDirectory = 2,
    CreateNew = 3,
    Create = 4,
    OpenOrCreate = 5,
    Truncate = 6,
    Append = 7,
    Move = 8,
    Replace = 9,
    Delete = 10,
    ReadDirectory = 11,
    ReadEntry = 12,
    MoveDirectory = 13
}

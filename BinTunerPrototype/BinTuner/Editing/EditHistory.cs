namespace BinTuner.Editing;

public class CellChange
{
    public int Address;
    public byte[] OldBytes = Array.Empty<byte>();
    public byte[] NewBytes = Array.Empty<byte>();
}

public class EditCommand
{
    public string Description = "";
    public List<CellChange> Changes = new();
}

/// <summary>Byte-level undo/redo, capped at 50 steps as required by the tuning-table spec.</summary>
public class EditHistory
{
    private const int MaxHistory = 50;
    private readonly List<EditCommand> _undo = new();
    private readonly List<EditCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(EditCommand command)
    {
        _undo.Add(command);
        if (_undo.Count > MaxHistory)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo(byte[] data)
    {
        if (_undo.Count == 0) return;
        var cmd = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        foreach (var change in cmd.Changes)
            Array.Copy(change.OldBytes, 0, data, change.Address, change.OldBytes.Length);
        _redo.Add(cmd);
    }

    public void Redo(byte[] data)
    {
        if (_redo.Count == 0) return;
        var cmd = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var change in cmd.Changes)
            Array.Copy(change.NewBytes, 0, data, change.Address, change.NewBytes.Length);
        _undo.Add(cmd);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}

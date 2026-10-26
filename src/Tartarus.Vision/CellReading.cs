namespace Tartarus.Vision;

/// <summary>
/// Reads what is in the cells of a Sandbox whose place is known: each cell's content, the warden, Prison Incidents,
/// the instrument and Chromatic panels. Returns a partial <see cref="ScanResult"/> (Cells, Warden, Instruments,
/// Chromatic). <see cref="CnnCellReader"/> reads the cells with small CNNs over cell crops, <see cref="SlotReader"/>
/// the panels.
/// </summary>
public interface ICellReader : IDisposable
{
    string Name { get; }
    ScanResult Read(Frame frame, ScreenLayout layout);
}

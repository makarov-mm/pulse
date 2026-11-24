using System.ComponentModel;

namespace Pulse.Models;

// A list row that is reused for whatever item currently occupies its position.
// Only properties whose value changed are announced, so a refresh repaints just those cells.
public abstract class ObservableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private protected void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

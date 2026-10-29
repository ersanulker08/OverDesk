using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Overdesk.Core;

/// <summary>Minimal INotifyPropertyChanged base so Core needs no UI framework.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

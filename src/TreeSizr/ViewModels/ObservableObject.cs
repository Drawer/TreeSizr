using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TreeSizr.ViewModels;

/// <summary>Base class for view models: raises <see cref="PropertyChanged"/> for bindings.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Sets <paramref name="field"/> and raises PropertyChanged; returns false when the value did not change.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

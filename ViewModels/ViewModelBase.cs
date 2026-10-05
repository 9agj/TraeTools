using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TraeTools.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    public event Action<string>? NavigateRequested;

    protected void RaiseNavigateRequested(string page) => NavigateRequested?.Invoke(page);
}

using CommunityToolkit.Mvvm.ComponentModel;
using FragForge.Core.Localization;
using FragForge.Core.Models;

namespace FragForge.UI.Core;

public sealed partial class TweakItemViewModel : ObservableObject
{
    public TweakDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Category => Definition.Category;
    public string Evidence => Definition.Evidence.ToString();
    public string Risk => Definition.Risk.ToString();
    public string Title => Definition.Title.Resolve(_culture);
    public string Description => Definition.Description.Resolve(_culture);
    public string Notes => Definition.Notes?.Resolve(_culture) ?? "";
    public string WhenNotToApply => Definition.WhenNotToApply?.Resolve(_culture) ?? "";
    public bool RequiresReboot => Definition.RequiresReboot;

    /// <summary>
    /// Aviso anticheat (Capa 10.2): lo que define el catálogo o, si no define nada, el contrato común. Se muestra
    /// siempre, para que el usuario no tenga que suponer qué hace el tweak con el juego.
    /// </summary>
    public string AnticheatNote => FragForge.Core.Models.AnticheatNote.Resolve(Definition.AnticheatNote, _culture);
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private TweakState state = TweakState.Unknown;
    [ObservableProperty] private string stateDisplay = "Unknown";
    [ObservableProperty] private string reason = "";
    private string _culture;
    public TweakItemViewModel(TweakDefinition definition, TweakStatus? status, Localizer localizer)
    {
        Definition = definition;
        _culture = localizer.Culture;
        if (status is not null) Update(status, localizer);
    }
    public void Update(TweakStatus status, Localizer localizer)
    {
        _culture = localizer.Culture;
        State = status.State;
        StateDisplay = localizer["state." + status.State];
        Reason = status.Reason;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(WhenNotToApply));
        OnPropertyChanged(nameof(AnticheatNote));
    }
}

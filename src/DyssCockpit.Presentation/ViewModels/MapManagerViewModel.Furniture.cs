using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>The furniture tab: the pieces of the map, placed, turned, moved or removed, all sent back as one list.</summary>
public sealed partial class MapManagerViewModel
{
    // ---- The furniture on the map ---------------------------------------------------

    public ObservableCollection<ManagedFurniture> FurnitureItems { get; } = new();
    [ObservableProperty] private ManagedFurniture? _selectedFurniture;
    public IReadOnlyList<FurnitureKind> FurnitureKinds { get; } = FurnitureKind.All;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlacementShape))] private FurnitureKind _furnitureKind = FurnitureKind.All[0];

    /// <summary>The outline to show under the cursor while placing a piece, as corners relative to its centre, at its default size.</summary>
    public IReadOnlyList<DyssCockpit.Core.Point>? PlacementShape =>
        PlacingFurniture ? MapShapes.Centred(new(0, 0), FurnitureKind.Length, FurnitureKind.Width) : null;

    public IReadOnlyList<OrientationOption> Orientations { get; } = OrientationOption.All;

    /// <summary>
    /// How the chosen piece faces. Picking another one turns it at once; set back quietly when
    /// another piece is chosen or the map reloads, so only the user's picks are sent.
    /// </summary>
    [ObservableProperty] private OrientationOption? _furnitureOrientation;
    private bool _showingOrientation;

    partial void OnFurnitureOrientationChanged(OrientationOption? value)
    {
        if (_showingOrientation || value is null || SelectedFurniture is not { } chosen || value.QuarterTurns == chosen.QuarterTurns) return;
        _ = OrientFurnitureAsync(chosen, value.QuarterTurns);
    }

    private void ShowOrientation(ManagedFurniture? piece)
    {
        _showingOrientation = true;
        FurnitureOrientation = piece?.QuarterTurns is { } turns ? Orientations[turns] : null;
        _showingOrientation = false;
    }

    /// <summary>Whether the orientation list can be used: a piece chosen, and the furniture editable.</summary>
    public bool CanOrientFurniture => HasFurniture();

    /// <summary>Why the furniture cannot be changed on this map, or empty when it can.</summary>
    public string FurnitureBlockedReason => FurnitureItems.FirstOrDefault(f => !f.CanBeSentBack) is { } odd
        ? Tf($"Cette carte porte un meuble que cette application ne sait pas décrire ({odd.Label}) : toute modification l'effacerait, les meubles restent donc en lecture seule.", $"This map holds a piece of furniture this application cannot describe ({odd.Label}): any change would erase it, so the furniture is read-only.")
        : "";

    public string FurnitureEmptyText => FurnitureItems.Count == 0 ? T("Aucun meuble sur cette carte.", "No furniture on this map.") : "";
    private bool FurnitureEditable => IsActiveMap && Idle && FurnitureBlockedReason == "";
    private bool CanAddFurniture() => FurnitureEditable;
    private bool HasFurniture() => SelectedFurniture is not null && FurnitureEditable;

    private ManagedFurniture? FurnitureAt(DyssCockpit.Core.Point p) =>
        FurnitureItems.Where(f => f.Corners.Count > 2 && MapShapes.Contains(f.Corners, p.X, p.Y))
            .OrderBy(f => MapShapes.Area(f.Corners)).FirstOrDefault();

    [RelayCommand]
    private void ClickFurniture(ManagedFurniture? piece)
    {
        if (piece is not null && !Busy && !InMode) SelectedFurniture = ReferenceEquals(SelectedFurniture, piece) ? null : piece;
    }

    partial void OnSelectedFurnitureChanged(ManagedFurniture? oldValue, ManagedFurniture? newValue)
    {
        if (oldValue is not null) oldValue.IsChosen = false;
        if (newValue is not null) newValue.IsChosen = true;
        ShowOrientation(newValue);
        RebuildScene();
        RefreshCommandStates();
    }

    // ---- Furniture commands --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAddFurniture))]
    private void AddFurniture()
    {
        PlacingFurniture = true;
        Hint = Tf($"Cliquez l'endroit où poser « {FurnitureKind.Label} » (le centre du meuble). Échap pour annuler.", $"Click where to place the “{FurnitureKind.Label}” (the centre of the piece). Esc to cancel.");
        Status = "";
        RefreshCommandStates();
    }

    /// <summary>Called by the map with the point clicked while placing a piece.</summary>
    public async Task FurniturePointPickedAsync(DyssCockpit.Core.Point p)
    {
        var (placing, kind) = (PlacingFurniture, FurnitureKind);
        LeaveModes();
        if (!placing) return;
        var pieces = CurrentFurniture();
        var index = pieces.Count == 0 ? 1 : pieces.Max(f => f.Index) + 1;
        pieces.Add(new FurniturePiece(index, kind, MapShapes.Centred(p, kind.Length, kind.Width)));
        await SendFurnitureAsync(Tf($"« {kind.Label} » ajouté", $"“{kind.Label}” added"), pieces);
    }

    /// <summary>
    /// Turns the chosen piece to face one of the four ways, about its centre and keeping its size.
    /// Laid out afresh rather than turned from where it stands, so a piece the phone left at an
    /// odd angle comes back square.
    /// </summary>
    private async Task OrientFurnitureAsync(ManagedFurniture chosen, int quarterTurns)
    {
        if (!HasFurniture())
        {
            ShowOrientation(chosen);
            return;
        }
        var (length, width) = MapShapes.Sides(chosen.Corners);
        var corners = MapShapes.Oriented(MapShapes.Centre(chosen.Corners), length, width, quarterTurns);
        await SendFurnitureAsync(Tf($"« {chosen.Label} » tourné à {quarterTurns * 90}°", $"“{chosen.Label}” turned to {quarterTurns * 90}°"), ReplaceCorners(chosen, corners));
        // Refused or not sent: the list goes back to how the piece really stands.
        if (ReferenceEquals(SelectedFurniture, chosen)) ShowOrientation(chosen);
    }

    [RelayCommand(CanExecute = nameof(HasFurniture))]
    private async Task DeleteFurnitureAsync()
    {
        if (SelectedFurniture is not { } chosen) return;
        if (!await _hub.Dialogs.ConfirmAsync(T("Supprimer le meuble", "Remove the furniture"), Tf($"Retirer « {chosen.Label} » de la carte ?", $"Remove the “{chosen.Label}” from the map?"))) return;
        await SendFurnitureAsync(Tf($"« {chosen.Label} » retiré", $"“{chosen.Label}” removed"), CurrentFurniture().Where(f => f.Index != chosen.Index).ToList());
    }

    /// <summary>The furniture in index order, the order the cloud lists it in, whatever the list on screen shows.</summary>
    private List<FurniturePiece> CurrentFurniture() => [.. FurnitureItems.OrderBy(f => f.Index).Select(f => new FurniturePiece(f.Index, f.Kind!, f.Corners))];

    private List<FurniturePiece> ReplaceCorners(ManagedFurniture chosen, IReadOnlyList<DyssCockpit.Core.Point> corners) =>
        [.. CurrentFurniture().Select(f => f.Index == chosen.Index ? f with { Corners = corners } : f)];

    private Task SendFurnitureAsync(string label, IReadOnlyList<FurniturePiece> pieces) =>
        EditAsync(label, c => c.AdjustFurnitureAsync(pieces, ct: _hub.Ct), refused: T("Le robot a refusé les meubles.", "The robot refused the furniture."));
}

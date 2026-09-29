using ReyEngine.Formats.MapGeo;

namespace ReyEngine.App.ViewModels;

/// <summary>
/// M798: the placed props (<c>MapAnimatedProp</c>) follow the TFT board stage (M797).
///
/// <para>A board's props carry <c>mVisibilityFlags</c> like its meshes and particles do: on anniversary the intro
/// props are IntroProp 127, LEVEL7_BALL 8, IntroPropBG 68 and the interaction props 4 or 8; on
/// dawnbringernightbringer the level-1 statues are 64 and the level-7 ones 4. With a stage on, a placement shows
/// only when its flags pass the SAME exact rule as the meshes (<see cref="MapVisibility.VisibleForStage"/>: they
/// share a bit with the stage's mask; 0, 255 and "no flags authored" - which reads as 255 - are all layers).
/// Without this a level-7 board drew its level-1 props beside its own.</para>
///
/// <para><b>Only under a stage.</b> "Start", and every map without stages, leaves the props exactly as they were - and
/// they were never filtered by the Map Visibility layer dropdown, which this does not start doing.</para>
///
/// <para><b>Nothing is decoded again.</b> The decoded instances (<c>_propInstances</c>, one per editor-visible
/// placement) are built once by <c>RefreshPropMeshesAsync</c> as before; the stage only selects among them when the set
/// is published, so switching stage costs a re-publish and no mesh or texture decode. Both backends draw
/// <c>CurrentPropMeshes</c>, so both follow; the prop icons and the picking of a prop use the same gate
/// (<see cref="StageShowsProp"/>), or an invisible prop would still take a click (M383).</para>
///
/// <para><b>Not emulated:</b> the stage's <c>MapActionPlayAnimation</c> actions (IntroProp "Transition" / "level1_idle").
/// A prop keeps playing its idle.</para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The stage mask the prop set was last published under; null when it was published unfiltered.</summary>
    private int? _publishedPropStage;

    /// <summary>Does the stage that is on show this placed prop? Always true with no stage.</summary>
    private bool StageShowsProp(AnimatedPropViewModel prop) =>
        _activeBoardStage is not { } stage || MapVisibility.VisibleForStage(prop.EffectiveVisibilityFlags, stage.Mask);

    /// <summary>The decoded prop instances that belong on screen under the stage that is on. Records the stage it
    /// filtered with, so <see cref="RefreshStageProps"/> knows when the published set is out of date.</summary>
    private List<PropInstanceData> StagePropInstances()
    {
        _publishedPropStage = CurrentStageMask;
        // the owners are parallel to the instances except while props are off; then show what there is rather than guess
        bool filter = _activeBoardStage is not null && _propInstanceOwners.Count == _propInstances.Count;
        var shown = new List<PropInstanceData>(_propInstances.Count);
        for (int i = 0; i < _propInstances.Count; i++)
            if (!filter || StageShowsProp(_propInstanceOwners[i])) shown.Add(_propInstances[i]);
        return shown;
    }

    /// <summary>The stage changed (or a tab was restored): publish the props again, filtered for it. A no-op on every
    /// map without stages and while the stage is the one already published.</summary>
    private void RefreshStageProps()
    {
        var mask = CurrentStageMask;
        if (mask == _publishedPropStage) return;
        _publishedPropStage = mask;
        if (!HasBoardStages) return;   // a map without stages has nothing to republish and nothing to report

        if (ShowPropMeshes) PublishAddedMeshPreview();   // the decoded meshes stay; only the selection among them changes

        if (_activeBoardStage is not { } stage) return;
        var placed = MapContent.AllProps.Where(p => p.IsEditorVisible && !p.IsDisabled && !p.IsRemoved).ToList();
        if (placed.Count == 0) return;
        _log.Info("Props", $"Board stage '{stage.Label}': {placed.Count(StageShowsProp)} of {placed.Count} prop placement(s) shown - "
            + $"the others carry layers the stage's mask {stage.Mask} does not include. The stage's prop animations "
            + "(MapActionPlayAnimation, e.g. IntroProp 'Transition') are not played: props keep their idle.");
    }
}

using System.Reflection;
using ReyEngine.Core.Settings;

namespace ReyEngine.Formats.Tests;

/// <summary>
/// M505: the Preferences dialog reaches the live settings through exactly one call —
/// <c>Settings.CopyFrom(dialog.ToSettings()); Settings.Save();</c>. A field CopyFrom forgets is a
/// preference that silently cannot be changed and cannot be persisted: the checkbox ticks, the dialog
/// saves, and the value is dropped between the two with no error anywhere.
///
/// <para>That is what happened to the M503c auto-save toggle, and it is not a mistake that shows up in a
/// build, a type check or a hand-written test of the fields someone remembered. So this test does not name
/// fields at all — it walks them.</para>
/// </summary>
public sealed class EditorSettingsTests
{
    private static IEnumerable<PropertyInfo> Settables => typeof(EditorSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0);

    [Fact]
    public void CopyFromCarriesEverySetting()
    {
        var source = new EditorSettings();
        var target = new EditorSettings();

        // Give every property a value that differs from the default, so "copied" cannot be confused with
        // "both happen to be the default".
        foreach (var p in Settables) p.SetValue(source, Distinct(p, p.GetValue(source)));

        target.CopyFrom(source);

        var dropped = Settables
            .Where(p => !Equals(p.GetValue(target), p.GetValue(source)))
            .Select(p => p.Name)
            .ToList();

        Assert.True(dropped.Count == 0,
            "EditorSettings.CopyFrom drops " + string.Join(", ", dropped)
            + " — those preferences cannot be changed or persisted from the Preferences dialog.");
    }

    [Fact]
    public void EveryPropertyActuallyChangesUnderDistinct()
    {
        // Guards the test above: if Distinct handed back the same value for some type, CopyFrom would look
        // correct for a field it never touched.
        var s = new EditorSettings();
        foreach (var p in Settables)
        {
            object? before = p.GetValue(s);
            p.SetValue(s, Distinct(p, before));
            Assert.NotEqual(before, p.GetValue(s));
        }
    }

    [Fact]
    public void TheAutoSaveDelayIsClampedToSomethingSurvivable()
    {
        // Zero would rewrite the whole mapgeo - 40 MB on Map453 - on every gizmo drag.
        Assert.Equal(2, new EditorSettings { AutoSaveDelaySeconds = 0 }.EffectiveAutoSaveDelaySeconds);
        Assert.Equal(120, new EditorSettings { AutoSaveDelaySeconds = 9999 }.EffectiveAutoSaveDelaySeconds);
        Assert.Equal(5, new EditorSettings().EffectiveAutoSaveDelaySeconds);
    }

    private static object? Distinct(PropertyInfo p, object? current) => p.PropertyType switch
    {
        var t when t == typeof(bool) => !(bool)(current ?? false),
        var t when t == typeof(int) => (int)(current ?? 0) + 7,
        var t when t == typeof(double) => (double)(current ?? 0d) + 7.5,
        var t when t == typeof(string) => (current as string ?? "") + "-changed",
        _ => throw new Xunit.Sdk.XunitException(
            $"EditorSettings.{p.Name} is a {p.PropertyType.Name}, which this test does not know how to vary. "
            + "Teach Distinct about it — otherwise the CopyFrom check silently skips the new field."),
    };
}

/// <summary>M762: the renderer choice is set from the viewport's View menu, not the Preferences dialog, so the
/// dialog must carry it through - otherwise any Preferences save would silently put an OpenGL user back on
/// Direct3D 11 (the M593 defect, for a new field).</summary>
public sealed class RendererChoiceSettingsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Saving_preferences_keeps_the_renderer_choice(bool openGl)
    {
        var dialog = new ReyEngine.App.ViewModels.SettingsViewModel(new EditorSettings { UseOpenGlViewport = openGl });
        Assert.Equal(openGl, dialog.ToSettings().UseOpenGlViewport);
    }

    [Fact]
    public void Direct3D_11_is_the_default() => Assert.False(new EditorSettings().UseOpenGlViewport);
}

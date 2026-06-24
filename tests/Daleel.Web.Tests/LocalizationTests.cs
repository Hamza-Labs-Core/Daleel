using System.Globalization;
using Daleel.Web.Resources;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Daleel.Web.Tests;

/// <summary>
/// Guards the shared-resource localization wiring. The marker type <see cref="SharedResource"/> lives in
/// the <c>Daleel.Web.Resources</c> namespace and its translations are embedded as
/// <c>Daleel.Web.Resources.SharedResource[.ar].resources</c>. The localizer must resolve to those exact
/// names — if the configured <c>ResourcesPath</c> doubles the "Resources" segment, lookups miss and the
/// UI shows raw keys ("Nav.Home") instead of translations. These tests fail in that broken state.
/// </summary>
public class LocalizationTests
{
    private static IStringLocalizer<SharedResource> BuildLocalizer(string? resourcesPath = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (resourcesPath is null)
        {
            // Mirror the fixed Program.cs: no ResourcesPath, because the marker type already sits in
            // the Resources namespace (embedded as Daleel.Web.Resources.SharedResource[.ar]).
            services.AddLocalization();
        }
        else
        {
            services.AddLocalization(o => o.ResourcesPath = resourcesPath);
        }

        return services.BuildServiceProvider().GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    [Fact]
    public void ResourcesPath_Resources_DoublesPrefix_AndMisses()
    {
        // Root cause: with ResourcesPath="Resources" the localizer looks for
        // Daleel.Web.Resources.Resources.SharedResource — a doubled segment — which does not exist,
        // so every lookup falls back to the raw key. This is exactly the live "Nav.Home" symptom.
        using var _ = new CultureScope("en");
        var localizer = BuildLocalizer(resourcesPath: "Resources");

        var home = localizer["Nav.Home"];

        home.ResourceNotFound.Should().BeTrue();
        home.Value.Should().Be("Nav.Home");
    }

    [Fact]
    public void Resolves_English_Nav_Keys()
    {
        using var _ = new CultureScope("en");
        var localizer = BuildLocalizer();

        var home = localizer["Nav.Home"];

        home.ResourceNotFound.Should().BeFalse("the embedded en resx must be found");
        home.Value.Should().Be("Home");
    }

    [Fact]
    public void Resolves_Arabic_Nav_Keys()
    {
        using var _ = new CultureScope("ar");
        var localizer = BuildLocalizer();

        var home = localizer["Nav.Home"];

        home.ResourceNotFound.Should().BeFalse("the embedded ar satellite resx must be found");
        home.Value.Should().Be("الرئيسية");
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _ui;
        private readonly CultureInfo _culture;

        public CultureScope(string name)
        {
            _ui = CultureInfo.CurrentUICulture;
            _culture = CultureInfo.CurrentCulture;
            var target = new CultureInfo(name);
            CultureInfo.CurrentUICulture = target;
            CultureInfo.CurrentCulture = target;
        }

        public void Dispose()
        {
            CultureInfo.CurrentUICulture = _ui;
            CultureInfo.CurrentCulture = _culture;
        }
    }
}

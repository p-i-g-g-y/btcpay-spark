using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Spark.Models;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Spark.ViewComponents;

/// <summary>
/// Retired widget — activity moved into <see cref="SparkDashboardWidgetViewComponent"/> so the
/// store dashboard shows one tidy Spark card instead of two. The view component is kept as a
/// no-op (rather than removed from SparkPlugin.cs registrations) to avoid breaking any persisted
/// dashboard layouts that reference its UI-extension slot.
/// </summary>
public class SparkActivityDashboardWidgetViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(StoreDashboardViewModel dashboardModel)
        => View(new SparkActivityDashboardWidgetViewModel { Configured = false });
}

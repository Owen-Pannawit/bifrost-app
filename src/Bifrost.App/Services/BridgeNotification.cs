using Android.App;
using Android.Content;
using Android.OS;

namespace Bifrost.App.Services;

/// <summary>How the bridge is doing, in the terms the operator sees.</summary>
public enum BridgeUiState { Starting, Ready, Attention, Error }

/// <summary>
/// Builds the persistent notification.
/// </summary>
/// <remarks>
/// <para>
/// <b>The most-seen surface in the product.</b> Most operators will never open the app; this is
/// their entire experience of it. See Docs/03-design/09-ui-ux-spec.md §4.
/// </para>
/// <para>
/// Rules kept from the spec: the title carries the state <i>in words</i> so it is glanceable; the
/// second line states the <i>action</i>, not the diagnosis; ready is silent and low-priority, so
/// the notification never buzzes when nothing is wrong.
/// </para>
/// </remarks>
internal static class BridgeNotification
{
    public const int Id = 1;

    /// <summary>Silent. Everything is fine and the operator does not need to know.</summary>
    private const string StatusChannelId = "bifrost.status";

    /// <summary>Alerts. Something the operator must act on.</summary>
    private const string AlertChannelId = "bifrost.alert";

    /// <remarks>
    /// Two channels rather than <c>SetPriority</c>, which has been deprecated since API 26 —
    /// importance belongs to the channel now. This is also the shape the operator can control:
    /// they can silence status without silencing alerts.
    /// </remarks>
    public static void EnsureChannel(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;

        // A notification that buzzes while everything is fine gets silenced by the operator, and
        // then the real ones are missed too.
        var status = new NotificationChannel(StatusChannelId, "Printer status", NotificationImportance.Low)
        {
            Description = "Shows whether the printer is connected.",
        };
        status.SetShowBadge(false);
        manager.CreateNotificationChannel(status);

        var alert = new NotificationChannel(AlertChannelId, "Printer needs attention", NotificationImportance.Default)
        {
            Description = "Out of paper, cover open, disconnected.",
        };
        manager.CreateNotificationChannel(alert);
    }

    public static Notification Build(Context context, BridgeUiState state, string title, string detail)
    {
        var icon = state switch
        {
            BridgeUiState.Ready => Android.Resource.Drawable.StatNotifySync,
            BridgeUiState.Attention => Android.Resource.Drawable.StatSysWarning,
            BridgeUiState.Error => Android.Resource.Drawable.StatNotifyError,
            _ => Android.Resource.Drawable.StatNotifySync,
        };

        var open = PendingIntent.GetActivity(
            context,
            0,
            new Intent(context, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

        // Ready and Starting are silent; anything the operator must act on goes to the alert
        // channel. Importance lives on the channel — SetPriority has been deprecated since API 26.
        var channel = state is BridgeUiState.Attention or BridgeUiState.Error
            ? AlertChannelId
            : StatusChannelId;

        return new Notification.Builder(context, channel)
            .SetContentTitle(title)
            .SetContentText(detail)
            .SetSmallIcon(icon)
            .SetContentIntent(open)
            // Not dismissible: it is a foreground-service requirement and a status indicator both.
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .Build();
    }

    public static void Update(Context context, BridgeUiState state, string title, string detail)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.Notify(Id, Build(context, state, title, detail));
    }
}

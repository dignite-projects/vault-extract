import { EnvironmentProviders, inject, makeEnvironmentProviders, provideAppInitializer } from '@angular/core';
import { NotificationEntityLinksService } from '@dignite/ng.notification-center';
import {
  NOTIFICATION_CENTER_NAV_ITEM_PROVIDERS,
  NOTIFICATION_CENTER_ROUTE_PROVIDERS,
} from '@dignite/ng.notification-center/config';

/**
 * `EntityTypeName` that Application stamps on every document notification
 * (`VaultExtractNotificationNames.DocumentEntityTypeName`). A persisted, frozen string.
 */
export const DOCUMENT_NOTIFICATION_ENTITY_TYPE = 'VaultExtract.Document';

/**
 * The notification bell (toolbar) and its inbox route, plus the click-through from a document notification to
 * that document (#680).
 *
 * `provideNotificationCenterConfig()` is deliberately not used: it also adds a "Subscriptions" settings tab. Every
 * notification Extract publishes goes to the uploader by explicit recipient, which bypasses subscriptions, so that
 * tab's toggles would do nothing. Add `NOTIFICATION_CENTER_SETTING_TAB_PROVIDERS` here the day something
 * publishes to subscribers.
 */
export function provideExtractNotifications(): EnvironmentProviders {
  return makeEnvironmentProviders([
    NOTIFICATION_CENTER_ROUTE_PROVIDERS,
    NOTIFICATION_CENTER_NAV_ITEM_PROVIDERS,
    provideAppInitializer(() => {
      inject(NotificationEntityLinksService).register(DOCUMENT_NOTIFICATION_ENTITY_TYPE, notification =>
        notification.entityId ? ['/documents', notification.entityId] : null,
      );
    }),
  ]);
}

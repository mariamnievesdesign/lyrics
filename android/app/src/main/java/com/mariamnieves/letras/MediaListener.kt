package com.mariamnieves.letras

import android.service.notification.NotificationListenerService

/**
 * No hace nada por sí solo: Android exige que la app tenga "acceso a notificaciones"
 * para dejarle leer qué canción está sonando en otras apps (Spotify, YouTube Music, Chrome…).
 */
class MediaListener : NotificationListenerService()

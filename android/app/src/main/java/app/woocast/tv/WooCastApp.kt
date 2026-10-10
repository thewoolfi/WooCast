// SPDX-License-Identifier: Apache-2.0
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import android.util.Log
import app.woocast.tv.engine.ReceiverEngine

/**
 * Точка входа процесса.
 *
 * Движок приёмника создаётся здесь и живёт столько же, сколько процесс: трансляция
 * не должна прерываться при пересоздании Activity (смена конфигурации, возврат из фона).
 */
class WooCastApp : Application() {

    /** Единственный экземпляр движка на процесс. */
    lateinit var engine: ReceiverEngine
        private set

    override fun onCreate() {
        super.onCreate()

        instance = this
        createNotificationChannel()

        engine = ReceiverEngine(this)
        engine.start()

        Log.i(TAG, "WooCast TV запущен, устройство: ${engine.deviceName}")
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return

        try {
            val manager = getSystemService(NotificationManager::class.java) ?: return
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notif_channel_name),
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = getString(R.string.notif_channel_description)
                setShowBadge(false)
            }

            manager.createNotificationChannel(channel)
        } catch (e: Exception) {
            // Канал — не критичная часть: без него трансляция всё равно работает.
            Log.w(TAG, "Не удалось создать канал уведомлений", e)
        }
    }

    companion object {
        private const val TAG = "WooCast/App"

        const val CHANNEL_ID = "woocast_stream"
        const val NOTIFICATION_ID = 1001

        const val ACTION_STOP = "app.woocast.tv.action.STOP"

        lateinit var instance: WooCastApp
            private set
    }
}

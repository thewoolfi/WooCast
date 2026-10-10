// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.service

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import app.woocast.tv.MainActivity
import app.woocast.tv.R
import app.woocast.tv.WooCastApp

/**
 * Постоянное уведомление на время работы приёмника.
 *
 * Сервис намеренно не владеет трансляцией: движок живёт в [WooCastApp]. Если система
 * по какой-то причине откажется поднять foreground-сервис (ограничения прошивок ТВ-приставок
 * встречаются), приём всё равно продолжит работать — сервис нужен только для уведомления
 * и удержания процесса.
 */
class StreamService : Service() {

    companion object {
        private const val TAG = "WooCast/Service"

        /** Запустить сервис, не падая, если система запрещает foreground-сервисы. */
        fun startQuietly(context: Context) {
            try {
                val intent = Intent(context, StreamService::class.java)
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    context.startForegroundService(intent)
                } else {
                    context.startService(intent)
                }
            } catch (e: Exception) {
                Log.w(TAG, "Не удалось запустить сервис уведомления", e)
            }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == WooCastApp.ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }

        val notification = buildNotification()
        if (notification == null) {
            // Без канала уведомлений foreground-режим невозможен — просто работаем как обычный сервис.
            return START_STICKY
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(
                    WooCastApp.NOTIFICATION_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC,
                )
            } else {
                startForeground(WooCastApp.NOTIFICATION_ID, notification)
            }
        } catch (e: Exception) {
            Log.w(TAG, "Не удалось перейти в foreground-режим", e)
        }

        return START_STICKY
    }

    override fun onDestroy() {
        try {
            stopForeground(STOP_FOREGROUND_REMOVE)
        } catch (e: Exception) {
            Log.w(TAG, "Ошибка при снятии уведомления", e)
        }

        super.onDestroy()
    }

    private fun buildNotification(): Notification? = try {        val openIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        val stopIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, StreamService::class.java).setAction(WooCastApp.ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        NotificationCompat.Builder(this, WooCastApp.CHANNEL_ID)
            .setContentTitle(getString(R.string.notif_title))
            .setContentText(getString(R.string.notif_text_idle))
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(openIntent)
            .addAction(0, getString(R.string.notif_stop), stopIntent)
            .setOngoing(true)
            .setSilent(true)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .build()
    } catch (e: Exception) {
        Log.w(TAG, "Не удалось собрать уведомление", e)
        null
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

package app.woocast.tv.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

/** Приёмник действий уведомления: остановка приёма трансляции. */
class StopReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context?, intent: Intent?) {
        if (context == null) return
        if (intent?.action != ACTION_STOP) return

        Log.i("WooCast/StopReceiver", "Остановка по действию пользователя")

        try {
            context.stopService(Intent(context, StreamService::class.java))
        } catch (e: Exception) {
            Log.w("WooCast/StopReceiver", "Не удалось остановить сервис", e)
        }
    }

    companion object {
        const val ACTION_STOP = "app.woocast.tv.action.STOP_STREAM"
    }
}

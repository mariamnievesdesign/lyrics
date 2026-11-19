package com.mariamnieves.letras

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** Pantalla de configuración: da los permisos y enciende/apaga la letra flotante. */
class MainActivity : Activity() {

    private lateinit var overlayStep: TextView
    private lateinit var notifStep: TextView
    private lateinit var mainButton: TextView

    private fun dpi(v: Int) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v.toFloat(), resources.displayMetrics).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = 0xFF14121B.toInt()
        window.navigationBarColor = 0xFF14121B.toInt()

        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpi(24), dpi(40), dpi(24), dpi(24))
        }

        col.addView(text("Letras ♪", 30f, 0xFFFFFFFF.toInt(), bold = true))
        col.addView(text("La letra de lo que suena, flotando encima de cualquier app.", 15f, 0xB3FFFFFF.toInt()).apply {
            setPadding(0, dpi(6), 0, dpi(28))
        })

        col.addView(text("1. Mostrar sobre otras apps", 16f, 0xFFFFFFFF.toInt(), bold = true))
        overlayStep = text("", 14f, 0xB3FFFFFF.toInt())
        col.addView(overlayStep)
        col.addView(button("Dar permiso") {
            startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
        })

        col.addView(text("2. Acceso a notificaciones", 16f, 0xFFFFFFFF.toInt(), bold = true).apply { setPadding(0, dpi(24), 0, 0) })
        notifStep = text("", 14f, 0xB3FFFFFF.toInt())
        col.addView(notifStep)
        col.addView(button("Dar acceso") {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        })
        col.addView(text(
            "¿Aparece \"Ajuste restringido\" o no te deja activarlo? Abre la información de la app → menú ⋮ " +
                "arriba a la derecha → \"Permitir ajustes restringidos\", y vuelve a intentarlo.",
            13f, 0x80FFFFFF.toInt()
        ).apply { setPadding(0, dpi(10), 0, 0) })
        col.addView(button("Abrir información de la app", subtle = true) {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
        })

        mainButton = button("") { toggleOverlay() }.apply {
            textSize = 17f
            (layoutParams as LinearLayout.LayoutParams).topMargin = dpi(36)
        }
        col.addView(mainButton)

        setContentView(ScrollView(this).apply {
            setBackgroundColor(0xFF14121B.toInt())
            addView(col)
        })

        // Android 13+: permiso para la notificación que mantiene viva la ventana.
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    private fun canOverlay() = Settings.canDrawOverlays(this)

    private fun hasNotificationAccess(): Boolean {
        val enabled = Settings.Secure.getString(contentResolver, "enabled_notification_listeners") ?: return false
        return enabled.contains(packageName)
    }

    private fun refresh() {
        overlayStep.text = if (canOverlay()) "✓ Listo" else "Para que la letra se vea encima de Spotify, Chrome, etc."
        notifStep.text = if (hasNotificationAccess()) "✓ Listo" else "Para saber qué canción suena y en qué segundo va."
        mainButton.text = if (OverlayService.running) "Ocultar letra flotante" else "Mostrar letra flotante"
    }

    private fun toggleOverlay() {
        if (OverlayService.running) {
            stopService(Intent(this, OverlayService::class.java))
        } else {
            if (!canOverlay() || !hasNotificationAccess()) {
                mainButton.text = "Primero completa los pasos 1 y 2"
                return
            }
            startForegroundService(Intent(this, OverlayService::class.java))
        }
        mainButton.postDelayed({ refresh() }, 300)
    }

    private fun text(s: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        setTextColor(color)
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    }

    private fun button(label: String, subtle: Boolean = false, onClick: () -> Unit) = TextView(this).apply {
        text = label
        textSize = 15f
        gravity = Gravity.CENTER
        setTextColor(0xFFFFFFFF.toInt())
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        setPadding(dpi(16), dpi(14), dpi(16), dpi(14))
        background = GradientDrawable().apply {
            cornerRadius = dpi(16).toFloat()
            setColor(if (subtle) 0x1FFFFFFF else 0xFF5B4B8A.toInt())
            if (subtle) setStroke(dpi(1), 0x33FFFFFF)
        }
        layoutParams = LinearLayout.LayoutParams(-1, -2).apply { topMargin = dpi(10) }
        setOnClickListener { onClick() }
    }
}

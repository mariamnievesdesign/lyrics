package com.mariamnieves.letras

import android.content.Context

/** Recuerda posición, tamaño, sincronía, tamaño de letra y transparencia. */
class Prefs(context: Context) {
    private val sp = context.getSharedPreferences("letras", Context.MODE_PRIVATE)

    var x: Int get() = sp.getInt("x", -1); set(v) = sp.edit().putInt("x", v).apply()
    var y: Int get() = sp.getInt("y", -1); set(v) = sp.edit().putInt("y", v).apply()
    var w: Int get() = sp.getInt("w", -1); set(v) = sp.edit().putInt("w", v).apply()
    var h: Int get() = sp.getInt("h", -1); set(v) = sp.edit().putInt("h", v).apply()
    var offsetMs: Long get() = sp.getLong("offset", 0); set(v) = sp.edit().putLong("offset", v).apply()
    var fontSp: Float get() = sp.getFloat("font", 20f); set(v) = sp.edit().putFloat("font", v).apply()
    var glass: Int get() = sp.getInt("glass", 1); set(v) = sp.edit().putInt("glass", v).apply()
}

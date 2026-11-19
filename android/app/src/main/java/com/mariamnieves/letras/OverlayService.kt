package com.mariamnieves.letras

import android.animation.ObjectAnimator
import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.PixelFormat
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.SystemClock
import android.util.TypedValue
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.view.animation.DecelerateInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import java.util.concurrent.Executors
import kotlin.math.abs

/** Ventana flotante con la letra, encima de cualquier app. */
class OverlayService : Service() {

    companion object {
        const val ACTION_STOP = "com.mariamnieves.letras.STOP"
        private const val CHANNEL = "letras"
        @Volatile var running = false

        private const val DIM_ALPHA = 0.45f
        private const val DIM_SCALE = 0.93f
        private const val LEAD_MS = 150L

        /** Opacidad del fondo de vidrio (0–255), de más transparente a más oscuro. */
        private val GLASS = intArrayOf(0x50, 0x80, 0xAA, 0xD5)
    }

    private val handler = Handler(Looper.getMainLooper())
    private val io = Executors.newSingleThreadExecutor()
    private lateinit var prefs: Prefs
    private lateinit var wm: WindowManager
    private lateinit var params: WindowManager.LayoutParams

    private lateinit var root: FrameLayout
    private lateinit var card: GradientDrawable
    private lateinit var titleText: TextView
    private lateinit var artistText: TextView
    private lateinit var controls: LinearLayout
    private lateinit var offsetText: TextView
    private lateinit var scroll: ScrollView
    private lateinit var lines: LinearLayout
    private lateinit var statusText: TextView

    // Estado de reproducción
    private var controller: MediaController? = null
    private var lastSessionCheck = 0L
    private var trackKey: String? = null
    private var lyrics: List<LrcLine> = emptyList()
    private var synced = false
    private var current = -2
    private var lookupId = 0
    private var scrollAnim: ObjectAnimator? = null

    // ---------------- Ciclo de vida ----------------

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        running = true
        prefs = Prefs(this)
        wm = getSystemService(WINDOW_SERVICE) as WindowManager
        startInForeground()
        buildOverlay()
        handler.post(tick)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) stopSelf()
        return START_STICKY
    }

    override fun onDestroy() {
        running = false
        handler.removeCallbacksAndMessages(null)
        io.shutdownNow()
        runCatching { wm.removeView(root) }
        super.onDestroy()
    }

    private fun startInForeground() {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL, "Letra flotante", NotificationManager.IMPORTANCE_MIN)
        )
        val stop = PendingIntent.getService(
            this, 0, Intent(this, OverlayService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE
        )
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_letras)
            .setContentTitle("Letras está activa")
            .setContentText("Mostrando la letra de lo que suena")
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, "Cerrar", stop).build())
            .setOngoing(true)
            .build()

        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(1, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(1, notification)
        }
    }

    // ---------------- Interfaz ----------------

    private fun dp(v: Float) = TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, v, resources.displayMetrics)
    private fun dpi(v: Int) = dp(v.toFloat()).toInt()

    @SuppressLint("ClickableViewAccessibility")
    private fun buildOverlay() {
        val screenW = resources.displayMetrics.widthPixels
        params = WindowManager.LayoutParams(
            if (prefs.w > 0) prefs.w else screenW - dpi(32),
            if (prefs.h > 0) prefs.h else dpi(190),
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = if (prefs.x >= 0) prefs.x else dpi(16)
            y = if (prefs.y >= 0) prefs.y else dpi(140)
        }

        card = GradientDrawable().apply {
            cornerRadius = dp(24f)
            setStroke(dpi(1), 0x40FFFFFF)
        }
        applyGlass()

        root = FrameLayout(this).apply { background = card; clipToOutline = true }

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpi(16), dpi(12), dpi(16), dpi(10))
        }

        // Encabezado
        titleText = TextView(this).apply {
            setTextColor(0xF2FFFFFF.toInt()); textSize = 13f; typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
        }
        artistText = TextView(this).apply {
            setTextColor(0xA6FFFFFF.toInt()); textSize = 11.5f
            maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END
        }
        column.addView(titleText)
        column.addView(artistText)

        // Controles (aparecen al tocar la ventana)
        controls = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            visibility = View.GONE
            setPadding(0, dpi(8), 0, dpi(2))
        }
        offsetText = TextView(this).apply {
            setTextColor(0xB3FFFFFF.toInt()); textSize = 11f; gravity = Gravity.CENTER
            minWidth = dpi(44)
        }
        controls.addView(roundButton("−") { changeOffset(-500) })
        controls.addView(offsetText)
        controls.addView(roundButton("+") { changeOffset(+500) })
        controls.addView(spacer())
        controls.addView(roundButton("A", 11f) { changeFont(-2f) })
        controls.addView(roundButton("A", 16f) { changeFont(+2f) })
        controls.addView(spacer())
        controls.addView(roundButton("◐") { prefs.glass = (prefs.glass + 1) % GLASS.size; applyGlass() })
        controls.addView(roundButton("✕") { stopSelf() })
        column.addView(controls)
        updateOffsetText()

        // Letra
        val lyricsFrame = FrameLayout(this)
        scroll = LockedScrollView(this).apply {
            isVerticalScrollBarEnabled = false
            overScrollMode = View.OVER_SCROLL_NEVER
            isFillViewport = false
        }
        lines = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        scroll.addView(lines, FrameLayout.LayoutParams(-1, -2))
        statusText = TextView(this).apply {
            setTextColor(0xCCFFFFFF.toInt()); textSize = 15f; gravity = Gravity.CENTER
            setShadowLayer(dp(6f), 0f, 0f, 0x99000000.toInt())
        }
        lyricsFrame.addView(scroll, FrameLayout.LayoutParams(-1, -1))
        lyricsFrame.addView(statusText, FrameLayout.LayoutParams(-1, -1))
        column.addView(lyricsFrame, LinearLayout.LayoutParams(-1, 0, 1f).apply { topMargin = dpi(4) })

        // Al cambiar de tamaño: espacio arriba/abajo para poder centrar la primera y la última línea.
        scroll.addOnLayoutChangeListener { _, _, top, _, bottom, _, oldTop, _, oldBottom ->
            if (bottom - top != oldBottom - oldTop) {
                val pad = (bottom - top) / 2
                lines.setPadding(0, pad, 0, pad)
                scroll.post { scrollToCurrent(false) }
            }
        }

        root.addView(column, FrameLayout.LayoutParams(-1, -1))

        // Esquina para cambiar el tamaño
        val grip = TextView(this).apply {
            text = "◢"; textSize = 12f; setTextColor(0x66FFFFFF); gravity = Gravity.CENTER
        }
        root.addView(grip, FrameLayout.LayoutParams(dpi(32), dpi(32), Gravity.BOTTOM or Gravity.END))
        grip.setOnTouchListener(resizeTouch())

        root.setOnTouchListener(dragTouch())

        showTrack(null)
        showStatus("Esperando música… ♪")
        wm.addView(root, params)
    }

    private fun roundButton(label: String, size: Float = 13f, onClick: () -> Unit) = TextView(this).apply {
        text = label; textSize = size; setTextColor(0xE6FFFFFF.toInt()); gravity = Gravity.CENTER
        background = GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(0x26FFFFFF)
            setStroke(dpi(1), 0x33FFFFFF)
        }
        layoutParams = LinearLayout.LayoutParams(dpi(34), dpi(34)).apply { marginEnd = dpi(6) }
        setOnClickListener { onClick() }
    }

    private fun spacer() = View(this).apply { layoutParams = LinearLayout.LayoutParams(dpi(8), 1) }

    private fun applyGlass() {
        val a = GLASS[prefs.glass.coerceIn(0, GLASS.size - 1)] shl 24
        card.orientation = GradientDrawable.Orientation.TL_BR
        card.colors = intArrayOf(a or 0x2E2A3E, a or 0x16141E, a or 0x1E1828)
    }

    // ---------------- Arrastrar, tocar y redimensionar ----------------

    @SuppressLint("ClickableViewAccessibility")
    private fun dragTouch() = object : View.OnTouchListener {
        var startX = 0f; var startY = 0f; var winX = 0; var winY = 0; var dragging = false
        val slop = ViewConfiguration.get(this@OverlayService).scaledTouchSlop

        override fun onTouch(v: View, e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    startX = e.rawX; startY = e.rawY; winX = params.x; winY = params.y; dragging = false
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = e.rawX - startX; val dy = e.rawY - startY
                    if (!dragging && (abs(dx) > slop || abs(dy) > slop)) dragging = true
                    if (dragging) {
                        params.x = (winX + dx).toInt().coerceAtLeast(0)
                        params.y = (winY + dy).toInt().coerceAtLeast(0)
                        wm.updateViewLayout(root, params)
                    }
                }
                MotionEvent.ACTION_UP -> {
                    if (dragging) { prefs.x = params.x; prefs.y = params.y }
                    else controls.visibility = if (controls.visibility == View.VISIBLE) View.GONE else View.VISIBLE
                }
            }
            return true
        }
    }

    @SuppressLint("ClickableViewAccessibility")
    private fun resizeTouch() = object : View.OnTouchListener {
        var startX = 0f; var startY = 0f; var w0 = 0; var h0 = 0

        override fun onTouch(v: View, e: MotionEvent): Boolean {
            when (e.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    startX = e.rawX; startY = e.rawY; w0 = root.width; h0 = root.height
                }
                MotionEvent.ACTION_MOVE -> {
                    params.width = (w0 + (e.rawX - startX)).toInt().coerceAtLeast(dpi(200))
                    params.height = (h0 + (e.rawY - startY)).toInt().coerceAtLeast(dpi(120))
                    wm.updateViewLayout(root, params)
                }
                MotionEvent.ACTION_UP -> { prefs.w = params.width; prefs.h = params.height }
            }
            return true
        }
    }

    // ---------------- Ciclo principal ----------------

    private val tick = object : Runnable {
        override fun run() {
            runCatching { update() }
            handler.postDelayed(this, 120)
        }
    }

    private fun update() {
        val now = SystemClock.elapsedRealtime()
        if (controller == null || now - lastSessionCheck > 1000) {
            lastSessionCheck = now
            controller = pickController()
        }

        val c = controller
        val meta = c?.metadata
        val title = meta?.getString(MediaMetadata.METADATA_KEY_TITLE)
            ?: meta?.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE)
        if (c == null || title.isNullOrBlank()) {
            if (trackKey != null) {
                trackKey = null
                clearLines(); showTrack(null); showStatus("Esperando música… ♪")
            }
            return
        }
        val m = meta!!
        val artist = m.getString(MediaMetadata.METADATA_KEY_ARTIST)
            ?: m.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST) ?: ""
        val key = "$artist\u0001$title"
        if (key != trackKey) {
            trackKey = key
            showTrack(title to artist)
            loadLyrics(key, title, artist, m.getLong(MediaMetadata.METADATA_KEY_DURATION))
        }

        highlight(positionOf(c))
    }

    private fun pickController(): MediaController? = try {
        val msm = getSystemService(MediaSessionManager::class.java)
        val sessions = msm.getActiveSessions(ComponentName(this, MediaListener::class.java))
        sessions.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
            ?: sessions.firstOrNull { it.packageName == controller?.packageName }
            ?: sessions.firstOrNull()
    } catch (e: SecurityException) {
        showStatus("Falta el acceso a notificaciones.\nÁbrelo desde la app Letras.")
        null
    }

    /** Android informa la posición exacta y cuándo la midió: la proyectamos al instante actual. */
    private fun positionOf(c: MediaController): Long {
        val s = c.playbackState ?: return 0
        var pos = s.position
        if (s.state == PlaybackState.STATE_PLAYING) {
            pos += ((SystemClock.elapsedRealtime() - s.lastPositionUpdateTime) * s.playbackSpeed).toLong()
        }
        return pos
    }

    private fun loadLyrics(key: String, title: String, artist: String, durationMs: Long) {
        val id = ++lookupId
        clearLines()
        showStatus("Buscando letra…")
        io.execute {
            val result = runCatching { LyricsApi.find(title, artist, durationMs) }
            handler.post {
                if (id != lookupId || key != trackKey) return@post
                val r = result.getOrNull()
                when {
                    r == null -> showStatus("No pude conectarme para buscar la letra")
                    !r.synced.isNullOrEmpty() -> { buildLines(r.synced, true); hideStatus() }
                    !r.plain.isNullOrBlank() -> {
                        buildLines(r.plain.split('\n').map { LrcLine(0, it.trim()) }, false); hideStatus()
                    }
                    r.instrumental -> showStatus("♪ Instrumental ♪")
                    else -> showStatus("No encontré la letra de esta canción")
                }
            }
        }
    }

    // ---------------- Letra ----------------

    private fun buildLines(list: List<LrcLine>, isSynced: Boolean) {
        clearLines()
        lyrics = list
        synced = isSynced
        for (line in list) {
            lines.addView(TextView(this).apply {
                text = line.text.ifBlank { "♪" }
                setTextColor(0xFFFFFFFF.toInt())
                textSize = prefs.fontSp
                typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
                gravity = Gravity.CENTER
                setPadding(0, dpi(4), 0, dpi(4))
                setShadowLayer(dp(6f), 0f, 0f, 0x99000000.toInt())
                alpha = if (isSynced) DIM_ALPHA else 0.9f
                scaleX = if (isSynced) DIM_SCALE else 1f
                scaleY = scaleX
            }, LinearLayout.LayoutParams(-1, -2))
        }
        scroll.post { scrollToCurrent(false) }
    }

    private fun clearLines() {
        scrollAnim?.cancel()
        lines.removeAllViews()
        lyrics = emptyList()
        synced = false
        current = -2
        scroll.scrollTo(0, 0)
    }

    private fun highlight(positionMs: Long) {
        if (!synced || lyrics.isEmpty()) return
        val pos = positionMs + prefs.offsetMs + LEAD_MS
        var idx = -1
        for (i in lyrics.indices) {
            if (lyrics[i].timeMs <= pos) idx = i else break
        }
        if (idx == current) return
        setActive(current, false)
        setActive(idx, true)
        current = idx
        scrollToCurrent(true)
    }

    private fun setActive(index: Int, active: Boolean) {
        val v = lines.getChildAt(index) ?: return
        val s = if (active) 1f else DIM_SCALE
        v.animate().alpha(if (active) 1f else DIM_ALPHA).scaleX(s).scaleY(s)
            .setDuration(350).setInterpolator(DecelerateInterpolator()).start()
    }

    private fun scrollToCurrent(animate: Boolean) {
        if (lines.childCount == 0) return
        if (!synced) { scroll.scrollTo(0, 0); return }
        val v = lines.getChildAt(current.coerceIn(0, lines.childCount - 1)) ?: return
        val target = (v.top + v.height / 2 - scroll.height * 0.42f).toInt().coerceAtLeast(0)
        scrollAnim?.cancel()
        if (!animate) { scroll.scrollTo(0, target); return }
        scrollAnim = ObjectAnimator.ofInt(scroll, "scrollY", target).apply {
            duration = 450
            interpolator = DecelerateInterpolator()
            start()
        }
    }

    // ---------------- Encabezado, estado y controles ----------------

    private fun showTrack(t: Pair<String, String>?) {
        titleText.text = t?.first ?: "Letras"
        artistText.text = t?.second ?: ""
        artistText.visibility = if (t?.second.isNullOrBlank()) View.GONE else View.VISIBLE
    }

    private fun showStatus(text: String) { statusText.text = text; statusText.visibility = View.VISIBLE }
    private fun hideStatus() { statusText.visibility = View.GONE }

    private fun changeOffset(deltaMs: Long) {
        prefs.offsetMs += deltaMs
        updateOffsetText()
    }

    private fun updateOffsetText() {
        val s = prefs.offsetMs / 1000.0
        offsetText.text = (if (s > 0) "+" else "") + String.format(java.util.Locale.US, "%.1f s", s)
    }

    private fun changeFont(delta: Float) {
        prefs.fontSp = (prefs.fontSp + delta).coerceIn(14f, 40f)
        for (i in 0 until lines.childCount) (lines.getChildAt(i) as TextView).textSize = prefs.fontSp
        scroll.post { scrollToCurrent(false) }
    }

    /** ScrollView que no responde al dedo: se mueve solo con la canción y deja arrastrar la ventana. */
    private class LockedScrollView(context: Context) : ScrollView(context) {
        override fun onInterceptTouchEvent(ev: MotionEvent?) = false
        @SuppressLint("ClickableViewAccessibility")
        override fun onTouchEvent(ev: MotionEvent?) = false
    }
}

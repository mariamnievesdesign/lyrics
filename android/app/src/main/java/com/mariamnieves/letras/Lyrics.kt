package com.mariamnieves.letras

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

data class LrcLine(val timeMs: Long, val text: String)

data class LyricsResult(val synced: List<LrcLine>?, val plain: String?, val instrumental: Boolean)

/** Lee letras en formato LRC: "[01:23.45] texto de la línea". */
object Lrc {
    private val stamp = Regex("""\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]""")

    fun parse(lrc: String): List<LrcLine> {
        val out = mutableListOf<LrcLine>()
        for (raw in lrc.split('\n')) {
            val line = raw.trimEnd('\r')
            val stamps = stamp.findAll(line).toList()
            if (stamps.isEmpty()) continue
            val text = stamp.replace(line, "").trim()
            for (m in stamps) {
                val min = m.groupValues[1].toLong()
                val sec = m.groupValues[2].replace(':', '.').toDouble()
                out += LrcLine(min * 60_000 + (sec * 1000).toLong(), text)
            }
        }
        return out.sortedBy { it.timeMs }
    }
}

/** Busca letras sincronizadas en LRCLIB (lrclib.net), una base de datos libre y gratuita. */
object LyricsApi {
    private val cache = HashMap<String, LyricsResult>()

    /** Bloqueante: llamar desde un hilo en segundo plano. */
    fun find(title: String, artist: String, durationMs: Long): LyricsResult {
        val key = "$artist\u0001$title"
        synchronized(cache) { cache[key]?.let { return it } }

        val (a, t) = clean(title, artist)
        val found = mutableListOf<JSONObject>()
        if (a.isNotEmpty()) found += search("api/search?track_name=${enc(t)}&artist_name=${enc(a)}")
        if (found.none(::hasSynced)) found += search("api/search?q=${enc("$a $t".trim())}")

        val seconds = durationMs / 1000.0
        val best = found.filter(::hasSynced).sortedBy {
            val d = it.optDouble("duration", 0.0)
            if (seconds > 0 && d > 0) Math.abs(d - seconds) else 0.0
        }.firstOrNull()

        val result = if (best != null) {
            LyricsResult(Lrc.parse(best.getString("syncedLyrics")), best.optStringOrNull("plainLyrics"), false)
        } else {
            val plain = found.firstOrNull { !it.optStringOrNull("plainLyrics").isNullOrBlank() }
            LyricsResult(null, plain?.optStringOrNull("plainLyrics"),
                found.isNotEmpty() && found.all { it.optBoolean("instrumental") })
        }
        synchronized(cache) { cache[key] = result }
        return result
    }

    private fun hasSynced(o: JSONObject) = !o.optStringOrNull("syncedLyrics").isNullOrBlank()

    private fun JSONObject.optStringOrNull(name: String): String? =
        if (isNull(name)) null else optString(name, "")

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8")

    private fun search(path: String): List<JSONObject> {
        val conn = URL("https://lrclib.net/$path").openConnection() as HttpURLConnection
        conn.connectTimeout = 10_000
        conn.readTimeout = 12_000
        conn.setRequestProperty("User-Agent", "Letras-Android/0.1 (+https://github.com/mariamnievesdesign/lyrics)")
        try {
            if (conn.responseCode == 404) return emptyList()
            if (conn.responseCode !in 200..299) throw java.io.IOException("HTTP ${conn.responseCode}")
            val body = conn.inputStream.bufferedReader().use { it.readText() }
            val arr = JSONArray(body)
            return (0 until arr.length()).map { arr.getJSONObject(it) }
        } finally {
            conn.disconnect()
        }
    }

    // ---- Limpieza de títulos (YouTube suele traer "(Official Video)", "Artista - Topic", etc.) ----

    private val noise = Regex(
        """\s*[(\[][^)\]]*(official|oficial|video|vídeo|lyric|letra|audio|visuali[sz]er|\bhd\b|4k|\bmv\b|remaster|en vivo|live)[^)\]]*[)\]]""",
        RegexOption.IGNORE_CASE)
    private val feat = Regex("""\s*[(\[]\s*(feat|ft|con|with)\.?\s[^)\]]*[)\]]""", RegexOption.IGNORE_CASE)
    private val suffix = Regex("""\s+[-–—]\s+[^-–—]*(remaster|version|versión|live|edit|mix)[^-–—]*$""", RegexOption.IGNORE_CASE)
    private val split = Regex("""^(.+?)\s+[-–—]\s+(.+)$""")
    private val channel = Regex("""(\s-\sTopic|VEVO|Official|Oficial)$""", RegexOption.IGNORE_CASE)
    private val channelStrip = Regex("""\s*-\s*Topic$|VEVO$""", RegexOption.IGNORE_CASE)

    private fun clean(rawTitle: String, rawArtist: String): Pair<String, String> {
        var title = noise.replace(rawTitle, "")
        title = feat.replace(title, "")
        title = suffix.replace(title, "").trim()

        var artist = rawArtist.trim()
        val looksLikeChannel = artist.isEmpty() || channel.containsMatchIn(artist)
        artist = channelStrip.replace(artist, "").trim()

        // En videos de YouTube el título suele ser "Artista - Canción".
        split.find(title)?.let { m ->
            val left = m.groupValues[1].trim()
            val matchesArtist = artist.isNotEmpty() &&
                (left.contains(artist, true) || artist.contains(left, true))
            if (matchesArtist || looksLikeChannel) {
                artist = left
                title = m.groupValues[2].trim()
            }
        }
        return artist to title
    }
}

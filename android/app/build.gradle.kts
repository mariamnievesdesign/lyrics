plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.mariamnieves.letras"
    compileSdk = 34

    defaultConfig {
        applicationId = "com.mariamnieves.letras"
        minSdk = 26
        targetSdk = 34
        versionCode = (System.getenv("GITHUB_RUN_NUMBER") ?: "1").toInt()
        versionName = "0.1.${System.getenv("GITHUB_RUN_NUMBER") ?: "0"}"
    }

    // Firma fija: así cada APK nuevo se instala encima del anterior sin desinstalar.
    signingConfigs {
        create("letras") {
            storeFile = file("letras.keystore")
            storePassword = "letras2026"
            keyAlias = "letras"
            keyPassword = "letras2026"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("letras")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

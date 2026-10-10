# SPDX-License-Identifier: MIT
# Copyright (c) 2025 WooCast contributors
#
# WooCast TV receiver is a plain Android/Compose application without reflection
# based de-serialisation, so no extra keep rules are strictly required.
# Kept intentionally minimal.
-dontwarn kotlinx.serialization.**
-keepattributes *Annotation*, InnerClasses
-keep,includedescriptorclasses class app.woocast.tv.**$$serializer { *; }
-keepclassmembers class app.woocast.tv.** {
    *** Companion;
}

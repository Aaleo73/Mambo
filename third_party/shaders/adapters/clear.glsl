// Mambo adaptation of AMD FidelityFX CAS, FP32 sharpen-only, exact arithmetic.
// Upstream commit: 9fabcc9a2c45f958aff55ddfda337e74ef894b7f, ffx-cas/ffx_cas.h.
// CAS_SLOW per-channel weights, CAS_GO_SLOWER sqrt/div, no better-diagonals.
// sharpness=0.0 is CAS's low-ringing setting, not an identity operation.
// Exactly one of the two passes runs per frame, at whichever of source and display has
// fewer pixels: sharpening the source before a downscale is mostly filtered away again.
//!DESC Mambo Clear source-linear CAS
//!HOOK MAIN
//!BIND HOOKED
//!WHEN OUTPUT.w OUTPUT.h * NATIVE_CROPPED.w NATIVE_CROPPED.h * < !
{{COMMON}}
{{CAS}}
vec3 mamboLoad(vec2 offset) {
    vec3 encoded = HOOKED_texOff(offset).rgb;
    if (!mamboEncodedValid(encoded)) return vec3(-1.0);
    return linearize(vec4(encoded, 1.0)).rgb;
}
vec4 hook() {
    vec4 original = HOOKED_tex(HOOKED_pos);
    if (!mamboEncodedValid(original.rgb)) return original;
    vec3 result;
    if (!mamboCas(mamboLoad(vec2(0.0, -1.0)), mamboLoad(vec2(-1.0, 0.0)), linearize(original).rgb,
                  mamboLoad(vec2(1.0, 0.0)), mamboLoad(vec2(0.0, 1.0)), mamboPeak(), result)) return original;
    vec4 encoded = delinearize(vec4(result, original.a));
    return vec4(encoded.rgb, original.a);
}

// libplacebo downscales in linear light, so SCALED already holds the same linear RGB
// the source pass builds with linearize; no transfer function is applied here.
//!DESC Mambo Clear scaled-linear CAS
//!HOOK SCALED
//!BIND HOOKED
//!WHEN OUTPUT.w OUTPUT.h * NATIVE_CROPPED.w NATIVE_CROPPED.h * <
{{COMMON}}
{{CAS}}
vec4 hook() {
    vec4 original = HOOKED_tex(HOOKED_pos);
    vec3 result;
    if (!mamboCas(HOOKED_texOff(vec2(0.0, -1.0)).rgb, HOOKED_texOff(vec2(-1.0, 0.0)).rgb, original.rgb,
                  HOOKED_texOff(vec2(1.0, 0.0)).rgb, HOOKED_texOff(vec2(0.0, 1.0)).rgb, mamboPeak(), result)) return original;
    return vec4(result, original.a);
}

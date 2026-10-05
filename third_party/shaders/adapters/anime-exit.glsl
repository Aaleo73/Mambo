// The original SDR clamp remains at PREKERNEL; HDR must clamp its proxy now.
//!DESC Mambo Anime HDR proxy clamp
//!HOOK MAIN
//!BIND HOOKED
//!BIND STATSMAX
{{COMMON}}
vec4 hook() {
    vec4 proxy = HOOKED_tex(HOOKED_pos);
    if (!mamboIsHdr()) return proxy;
    float current = dot(proxy.rgb, vec3(0.299, 0.587, 0.114));
    float bound = STATSMAX_tex(STATSMAX_pos).r;
    return vec4(proxy.rgb - max(current - bound, 0.0), proxy.a);
}

//!DESC Mambo Anime restore HDR source and bounded detail gain
//!HOOK MAIN
//!BIND HOOKED
//!BIND MAMBO_A4K_SOURCE
//!BIND MAMBO_A4K_ORIGINAL
//!BIND MAMBO_A4K_BASE
{{COMMON}}
vec4 hook() {
    vec4 processed = HOOKED_tex(HOOKED_pos);
    if (!mamboIsHdr()) return processed;
    vec4 original = MAMBO_A4K_ORIGINAL_tex(MAMBO_A4K_ORIGINAL_pos);
    if (!mamboFinite(original.rgb) || any(lessThan(original.rgb, vec3(0.0))))
        return MAMBO_A4K_SOURCE_tex(MAMBO_A4K_SOURCE_pos);
    float maximum = mamboMax(original.rgb);
    vec2 baseline = MAMBO_A4K_BASE_tex(MAMBO_A4K_BASE_pos).rg;
    float gain = 1.0;
    if (maximum >= 1e-5 && mamboFinite(processed.rgb)) {
        float u = (processed.r + processed.g + processed.b) / 3.0;
        float v = baseline.r;
        gain = clamp((mamboInverseProxy(u) + 1e-6) / (mamboInverseProxy(v) + 1e-6), 0.9, 1.1);
        gain = mix(1.0, gain, clamp(baseline.g, 0.0, 1.0));
        // Reduce only the added gain near the absolute source signal ceiling.
        // A shared gain preserves RGB ratios; per-channel clipping would not.
        gain = min(gain, max(1.0, mamboPeak() / maximum));
    }
    vec4 encoded = delinearize(vec4(original.rgb * gain, original.a));
    return vec4(encoded.rgb, original.a);
}

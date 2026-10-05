// Preserve HDR source pixels before presenting a bounded proxy to Anime4K.
//!DESC Mambo Anime preserve source
//!HOOK MAIN
//!BIND HOOKED
//!SAVE MAMBO_A4K_SOURCE
vec4 hook() { return HOOKED_tex(HOOKED_pos); }

//!DESC Mambo Anime preserve source-linear RGB
//!HOOK MAIN
//!BIND HOOKED
//!SAVE MAMBO_A4K_ORIGINAL
{{COMMON}}
vec4 hook() {
    vec4 source = HOOKED_tex(HOOKED_pos);
    if (!mamboIsHdr()) return source;
    if (!mamboEncodedValid(source.rgb)) return vec4(vec3(-1.0), source.a);
    return vec4(linearize(source).rgb, source.a);
}

//!DESC Mambo Anime initialize unenhanced proxy and flat-area gate
//!HOOK MAIN
//!BIND HOOKED
//!SAVE MAMBO_A4K_BASE
//!COMPONENTS 2
{{COMMON}}
vec4 hook() {
    if (!mamboIsHdr()) return vec4(0.0);
    vec3 encoded = HOOKED_tex(HOOKED_pos).rgb;
    if (!mamboEncodedValid(encoded)) return vec4(0.0);
    vec3 value = linearize(vec4(encoded, 1.0)).rgb;
    if (!mamboFinite(value) || any(lessThan(value, vec3(0.0)))) return vec4(0.0);
    float low = 1e20;
    float high = 0.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec3 neighborEncoded = HOOKED_texOff(vec2(x, y)).rgb;
            if (!mamboEncodedValid(neighborEncoded)) return vec4(mamboProxy(mamboMax(value)), 0.0, 0.0, 0.0);
            vec3 neighbor = linearize(vec4(neighborEncoded, 1.0)).rgb;
            if (!mamboFinite(neighbor) || any(lessThan(neighbor, vec3(0.0)))) return vec4(mamboProxy(mamboMax(value)), 0.0, 0.0, 0.0);
            float maximum = mamboMax(neighbor);
            low = min(low, maximum);
            high = max(high, maximum);
        }
    }
    float gate = smoothstep(0.01, 0.05, (high - low) / (high + 1e-6));
    return vec4(mamboProxy(mamboMax(value)), gate, 0.0, 0.0);
}

//!DESC Mambo Anime enter bounded HDR proxy
//!HOOK MAIN
//!BIND HOOKED
//!BIND MAMBO_A4K_BASE
{{COMMON}}
vec4 hook() {
    vec4 source = HOOKED_tex(HOOKED_pos);
    if (!mamboIsHdr()) return source;
    return vec4(vec3(MAMBO_A4K_BASE_tex(MAMBO_A4K_BASE_pos).r), source.a);
}

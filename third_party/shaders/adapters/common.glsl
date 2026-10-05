// Mambo source-space helpers. libplacebo MAIN uses the source transfer function.
float mamboMax(vec3 value) { return max(value.r, max(value.g, value.b)); }
bool mamboFinite(vec3 value) { return !any(isnan(value)) && !any(isinf(value)); }
bool mamboEncodedValid(vec3 value) {
    return mamboFinite(value) && all(greaterThanEqual(value, vec3(0.0))) && all(lessThanEqual(value, vec3(1.0)));
}
float mamboPeak() { return max(1.0, mamboMax(linearize(vec4(1.0)).rgb)); }
bool mamboIsHdr() { return mamboPeak() > 1.01; }
float mamboProxy(float value) { return pow(max(value, 0.0) / (1.0 + max(value, 0.0)), 1.0 / 2.2); }
float mamboInverseProxy(float value) {
    float powered = pow(clamp(value, 0.0, 0.9999), 2.2);
    return powered / max(1.0 - powered, 1e-6);
}

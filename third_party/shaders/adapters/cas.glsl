bool mamboValid(vec3 value, float peak) {
    return mamboFinite(value) && all(greaterThanEqual(value, vec3(0.0))) && all(lessThanEqual(value, vec3(peak)));
}
// Official noScaling CAS kernel on linear RGB taps, with protected division for black pixels.
// False means the centre pixel must be returned untouched: invalid, out-of-range or flat neighbourhood.
bool mamboCas(vec3 b, vec3 d, vec3 e, vec3 f, vec3 h, float peak, out vec3 result) {
    result = e;
    if (!mamboValid(b, peak) || !mamboValid(d, peak) || !mamboValid(e, peak) ||
        !mamboValid(f, peak) || !mamboValid(h, peak)) return false;
    b /= peak; d /= peak; e /= peak; f /= peak; h /= peak;
    vec3 low = min(min(min(d, e), min(f, b)), h);
    vec3 high = max(max(max(d, e), max(f, b)), h);
    if (mamboMax(high - low) < 1e-7) return false;
    vec3 amplitude = sqrt(clamp(min(low, vec3(1.0) - high) / max(high, vec3(1e-8)), 0.0, 1.0));
    vec3 weight = amplitude * (-1.0 / 8.0);
    vec3 sharpened = clamp(((b + d + f + h) * weight + e) / (vec3(1.0) + 4.0 * weight), 0.0, 1.0);
    result = mix(e, sharpened, 0.35) * peak;
    return true;
}

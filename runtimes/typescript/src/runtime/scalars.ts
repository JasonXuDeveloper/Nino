/** Milliseconds between 0001-01-01 and 1970-01-01. */
export const unixEpochMilliseconds = 62135596800000;
/** Milliseconds between 0001-01-01 and 10000-01-01: past `DateTime.MaxValue`. */
export const maxDateMilliseconds = 315537897600000;
/** The char codes of each byte's high and low hex digit. */
export const hexHigh = Uint8Array.from({ length: 256 }, (_, i) => "0123456789abcdef".charCodeAt(i >> 4));
export const hexLow = Uint8Array.from({ length: 256 }, (_, i) => "0123456789abcdef".charCodeAt(i & 15));
/** The value of each hex digit by its char code, else -1. */
export const hexValues = Int8Array.from({ length: 128 }, (_, c) =>
  c >= 48 && c <= 57 ? c - 48 : (c | 32) >= 97 && (c | 32) <= 102 ? (c | 32) - 87 : -1);

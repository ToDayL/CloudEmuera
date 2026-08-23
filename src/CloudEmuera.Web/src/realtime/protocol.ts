/**
 * Runtime-only browser protocol constants and the compatibility facade for
 * generated realtime types.  The closed payload/type surface is generated
 * from Contracts/Realtime/realtime-v4.schema.json; callers must still pass
 * decoded data through codec.ts before treating it as a TypeScript value.
 */
export * from "./generated";

export const REALTIME_SUBPROTOCOL = "cloudemuera.realtime.v4" as const;
export const MAX_REALTIME_MESSAGE_BYTES = 12 * 1024 * 1024 + 2 * 1024;
export const MAX_REALTIME_JSON_DEPTH = 32;

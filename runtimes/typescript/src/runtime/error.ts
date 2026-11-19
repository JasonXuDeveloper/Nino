/** Thrown for data that is not a valid value of the type read, or a value that cannot be written. */
export class NinoError extends Error {
  constructor(message: string) {
    super("Nino: " + message);
    this.name = "NinoError";
  }
}

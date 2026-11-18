/** A rectangular array's dimensions and its flat data, with the last index varying fastest. */
export interface NinoGrid<T extends ArrayLike<unknown>> {
  readonly lengths: readonly number[];
  data: T;
}

// .NET's Array.MaxLength and the maximum product of the dimensions (wire-format spec §9.13).
export const maxGridLength = 0x7fff_ffc7;
export const maxGridElements = 0xffff_ffff;

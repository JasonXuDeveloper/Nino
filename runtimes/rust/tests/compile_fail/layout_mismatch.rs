#[derive(nino::NinoRead, nino::NinoWrite, Default)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x00000001; members=X:int:raw(4);Y:int:raw(4)")]
struct S {
    x: i32,
    z: i32,
}
fn main() {}

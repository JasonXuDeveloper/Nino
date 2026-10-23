#[derive(nino::NinoRead, nino::NinoWrite, Default)]
#[nino(layout = "format=Positional; header=NullFlag; trackReferences=false; raw=false; typeId=0x00000001; deferrable=false; members=X:int:raw(4)", deferrable = true)]
struct S {
    x: i32,
}
fn main() {}

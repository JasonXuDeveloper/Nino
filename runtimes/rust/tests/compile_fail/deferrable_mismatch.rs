#[derive(nino::NinoRead, nino::NinoWrite, Default)]
#[nino(layout = "format=Positional; header=Presence; trackReferences=false; raw=false; id=0x00000001; deferrable=false; members=X:int:raw(4)", deferrable = true)]
struct S {
    x: i32,
}
fn main() {}

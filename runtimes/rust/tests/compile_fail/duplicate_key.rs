#[derive(nino::NinoRead, nino::NinoWrite, Default)]
#[nino(format = "keyed")]
struct S {
    #[nino(key = "Name")]
    a: i32,
    #[nino(key = "Name")]
    b: i32,
}
fn main() {}

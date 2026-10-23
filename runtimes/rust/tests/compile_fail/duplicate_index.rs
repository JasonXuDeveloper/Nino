#[derive(nino::NinoRead, nino::NinoWrite, Default)]
struct S {
    #[nino(index = 0)]
    a: i32,
    #[nino(index = 0)]
    b: i32,
}
fn main() {}

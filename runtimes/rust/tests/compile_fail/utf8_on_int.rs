#[derive(nino::NinoRead, nino::NinoWrite, Default)]
struct S {
    #[nino(utf8)]
    id: i32,
}
fn main() {}

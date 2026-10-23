#[derive(nino::NinoRead, nino::NinoWrite, Default)]
struct S {
    #[nino(rename = "A")]
    a: i32,
}
fn main() {}

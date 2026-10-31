#[derive(nino::NinoRead, nino::NinoWrite, Default)]
#[nino(format = "positional")]
struct S {
    #[nino(key = "Id")]
    id: i32,
}
fn main() {}

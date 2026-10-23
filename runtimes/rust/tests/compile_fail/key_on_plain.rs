#[derive(nino::NinoRead, nino::NinoWrite, Default)]
#[nino(format = "plain")]
struct S {
    #[nino(key = "Id")]
    id: i32,
}
fn main() {}

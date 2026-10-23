#[derive(nino::NinoRead, nino::NinoWrite, Default)]
struct A {
    x: i32,
}
#[derive(nino::NinoRead, nino::NinoWrite)]
enum Any {
    A(Box<A>),
}
fn main() {}

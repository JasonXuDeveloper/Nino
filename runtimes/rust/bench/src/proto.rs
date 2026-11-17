//! The messages of the .NET suite's protobuf-net contracts (build.rs): `bcl` is protobuf-net's .NET types,
//! `nino::cases` and `nino::workloads` the suite's.

#![allow(clippy::all, clippy::pedantic, missing_docs)]

pub mod bcl {
    include!(concat!(env!("OUT_DIR"), "/bcl.rs"));
}

pub mod nino {
    pub mod cases {
        include!(concat!(env!("OUT_DIR"), "/nino.cases.rs"));
    }

    pub mod workloads {
        include!(concat!(env!("OUT_DIR"), "/nino.workloads.rs"));
    }
}

/// Runs a generic function on a message's prost type ([`with_message`]).
pub trait ProtoVisit<R> {
    fn visit<M: prost::Message + Default + PartialEq + 'static>(self) -> R;
}

include!(concat!(env!("OUT_DIR"), "/registry.rs"));

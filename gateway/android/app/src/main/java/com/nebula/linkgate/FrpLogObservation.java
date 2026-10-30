package com.nebula.linkgate;

import java.util.Locale;

/** Recognizes only FRP 0.71.1 events whose meaning is known; never guesses paths. */
final class FrpLogObservation {
    enum Event { NONE, COORDINATOR_LOGIN, NAT_HOLE_OBSERVED, STUN_OBSERVATIONS_INSUFFICIENT }

    static Event classify(String raw) {
        String line = raw.toLowerCase(Locale.ROOT);
        if (line.contains("login to server success")) return Event.COORDINATOR_LOGIN;
        if (line.contains("nathole prepare error") && line.contains("not enough addresses")) return Event.STUN_OBSERVATIONS_INSUFFICIENT;
        // This INFO occurs before QUIC Session.Init. It is not a completed
        // encrypted tunnel or proof of where the application's bytes travelled.
        if (line.contains("establishing nat hole connection successful")) return Event.NAT_HOLE_OBSERVED;
        // DEBUG "try to transfer connection to visitor" means an attempt, not
        // success; names ending in -fallback and open-tunnel errors prove neither.
        return Event.NONE;
    }
    private FrpLogObservation() {}
}

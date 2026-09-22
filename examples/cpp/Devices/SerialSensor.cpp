#include "SerialSensor.h"

/* A block comment with an unbalanced brace { and the word class, which the
   reader must not take for a declaration. */

#define DECLARE_PORT_TRAITS(name) struct name##Traits { static const int kBaud = 9600; }

DECLARE_PORT_TRAITS(Serial);

namespace Example { namespace Devices {

SerialSensor::SerialSensor(int address, const char* port)
    : Sensor(address), _port(port) {}

Readings::Reading SerialSensor::lastReading() const { return _last; }

}}

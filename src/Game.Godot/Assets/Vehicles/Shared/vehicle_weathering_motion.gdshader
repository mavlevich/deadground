// Weathering plus running gear motion, for meshes exported by vehicle_kit.emit_motion_parts.
shader_type spatial;
render_mode diffuse_burley, specular_schlick_ggx;
#define VEHICLE_MOTION
#include "vehicle_weathering.gdshaderinc"

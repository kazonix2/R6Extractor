#version 330 core

uniform sampler2D texModel;
uniform sampler2D texReflection;
uniform sampler2D texUv;
uniform sampler2D texRadiance;
uniform sampler2D texNormal;
uniform sampler2D texSpecular;
uniform vec2 uvScale;
uniform vec3 cameraPosition;
uniform int hasNormal;
uniform int hasSpecular;
uniform int alphaMode;
uniform vec3 materialTint;
uniform float glassOpacity;
uniform sampler2D texBlend;
uniform sampler2D texBlendMask;
uniform vec2 blendUvScale;
uniform int hasBlend;
uniform int hasBlendMask;

uniform vec3 lightPos;
uniform vec3 giAmbientColor;
uniform float giAmbientIntensity;
uniform int hasBakedGi;

#define MAX_DYNAMIC_LIGHTS 64
uniform int dynamicLightCount;
uniform vec3 dynamicLightPosition[MAX_DYNAMIC_LIGHTS];
uniform vec3 dynamicLightDirection[MAX_DYNAMIC_LIGHTS];
uniform vec3 dynamicLightColor[MAX_DYNAMIC_LIGHTS];
uniform float dynamicLightIntensity[MAX_DYNAMIC_LIGHTS];
uniform float dynamicLightRadius[MAX_DYNAMIC_LIGHTS];
uniform float dynamicLightConeCosine[MAX_DYNAMIC_LIGHTS];
uniform float dynamicLightInnerConeCosine[MAX_DYNAMIC_LIGHTS];
uniform int dynamicLightKind[MAX_DYNAMIC_LIGHTS];

uniform int useReflections;
uniform int useCheckerboard;
uniform int hasTexture;

in vec3 fragPos;
in vec3 fragNormal;
in vec3 fragVertColor;
in vec2 fragTexCoord;
in vec2 matcapTexCoord;
flat in int fragObjectId;

out vec4 color;

#define NUM_COLOR_LOOKUPS 8
vec3 COLOR_LOOKUP[NUM_COLOR_LOOKUPS] = vec3[NUM_COLOR_LOOKUPS](
vec3(0.85, 0.32, 0.30),
vec3(0.36, 0.75, 0.87),
vec3(0.36, 0.72, 0.36),
vec3(0.26, 0.55, 0.79),
vec3(1.00, 0.75, 0.14),
vec3(0.95, 0.46, 0.20),
vec3(0.89, 0.12, 0.91),
vec3(0.28, 0.56, 0.56)
);

vec4 GRAY80 = vec4(vec3(0.8), 1.0);
vec4 WHITE = vec4(vec3(1.0), 1.0);

vec3 srgbToLinear(vec3 value)
{
    return mix(value / 12.92, pow((value + 0.055) / 1.055, vec3(2.4)),
               step(vec3(0.04045), value));
}

vec3 linearToSrgb(vec3 value)
{
    value = max(value, vec3(0.0));
    return mix(value * 12.92, 1.055 * pow(value, vec3(1.0 / 2.4)) - 0.055,
               step(vec3(0.0031308), value));
}

void main()
{
    vec2 uv = fragTexCoord * uvScale;
    vec4 modelSample = hasTexture == 1 ? texture(texModel, uv) : vec4(1.0);
    modelSample.rgb = srgbToLinear(modelSample.rgb);
    if (hasBlend == 1)
    {
        vec4 layer = texture(texBlend, fragTexCoord * blendUvScale);
        float weight = hasBlendMask == 1 ? texture(texBlendMask, uv).r : fragVertColor.r;
        vec3 layerColor = srgbToLinear(layer.rgb);
        modelSample.rgb = hasTexture == 1 ? mix(modelSample.rgb, layerColor, clamp(weight, 0.0, 1.0)) : layerColor;
    }
    vec4 radianceSamp = mix(GRAY80, texture(texRadiance, matcapTexCoord), useReflections);
    vec4 checkerSamp = mix(WHITE, texture(texUv, uv), useCheckerboard);
    vec4 reflectionSamp = texture(texReflection, matcapTexCoord);
    vec3 norm = normalize(fragNormal);
    if (hasNormal == 1)
    {
        vec3 dx = dFdx(fragPos), dy = dFdy(fragPos);
        vec2 ux = dFdx(uv), uy = dFdy(uv);
        float determinant = ux.x * uy.y - ux.y * uy.x;
        if (abs(determinant) > 1e-10)
        {
            vec3 tangent = (dx * uy.y - dy * ux.y) / determinant;
            tangent -= norm * dot(tangent, norm);
            if (dot(tangent, tangent) > 1e-10)
            {
                tangent = normalize(tangent);
                vec3 bitangent = cross(norm, tangent);
                if (dot(bitangent, (dy * ux.x - dx * uy.x) / determinant) < 0.0)
                    bitangent = -bitangent;
                vec2 xy = texture(texNormal, uv).rg * 2.0 - 1.0;
                vec3 mapped = vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
                norm = normalize(mat3(tangent, bitangent, norm) * mapped);
            }
        }
    }

    if ((alphaMode == 1 && modelSample.a < 0.5) || (alphaMode == 2 && glassOpacity < 0.0 && modelSample.a <= 0.0))
        discard;
    vec3 lighting = giAmbientColor * giAmbientIntensity;
    vec3 specular = vec3(0.0);
    vec3 viewDirection = normalize(cameraPosition - fragPos);
    float specularStrength = hasSpecular == 1 ? dot(texture(texSpecular, uv).rgb, vec3(0.333333)) : 0.0;
    if (hasBakedGi == 0 && dynamicLightCount == 0)
        lighting += vec3(max(dot(norm, normalize(lightPos)), 0.0) * 0.25);
    for (int index = 0; index < dynamicLightCount; ++index)
    {
        vec3 toLight = dynamicLightPosition[index] - fragPos;
        float distanceSquared = dot(toLight, toLight);
        float radius = max(dynamicLightRadius[index], 0.001);
        float range = distanceSquared / (radius * radius);
        if (range >= 1.0)
            continue;
        vec3 directionToLight = toLight * inversesqrt(max(distanceSquared, 1e-6));
        float falloff = 1.0 - range * range;
        float attenuation = falloff * falloff / max(distanceSquared, 0.25);
        float cone = 1.0;
        if (dynamicLightKind[index] == 1)
        {
            float coneDot = dot(normalize(dynamicLightDirection[index]), -directionToLight);
            float outer = dynamicLightConeCosine[index];
            cone = smoothstep(outer, max(outer + 1e-5, dynamicLightInnerConeCosine[index]), coneDot);
        }
        float lambert = max(dot(norm, directionToLight), 0.0);
        vec3 incident = dynamicLightColor[index] * dynamicLightIntensity[index] * attenuation * cone;
        lighting += incident * lambert;
        if (specularStrength > 0.0 && lambert > 0.0)
        {
            vec3 halfDirection = normalize(directionToLight + viewDirection);
            float highlight = pow(max(0.0, dot(norm, halfDirection)), 32.0);
            specular += incident * (lambert * highlight * specularStrength);
        }
    }

    bool textured = hasTexture == 1 || hasBlend == 1;
    vec3 baseColor = textured ? modelSample.rgb : (glassOpacity >= 0.0 || materialTint != vec3(1.0)
        ? vec3(1.0) : srgbToLinear(mix(checkerSamp.rgb, COLOR_LOOKUP[fragObjectId % NUM_COLOR_LOOKUPS], 0.5)));
    baseColor *= materialTint;
    vec3 litColor = baseColor * lighting + specular;
    if (useReflections == 1 && hasBakedGi == 0 && dynamicLightCount == 0)
        litColor *= mix(vec3(1.0), radianceSamp.rgb, 0.75) * mix(vec3(1.0), reflectionSamp.rgb, 0.3);
    float opacity = alphaMode == 2 ? modelSample.a : 1.0;
    if (glassOpacity >= 0.0)
    {
        float facing = abs(dot(norm, viewDirection));
        opacity = mix(glassOpacity, 0.85, pow(1.0 - clamp(facing, 0.0, 1.0), 5.0));
    }
    color = vec4(linearToSrgb(litColor), opacity);
}

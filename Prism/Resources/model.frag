#version 330 core

uniform sampler2D texModel;
uniform sampler2D texReflection;
uniform sampler2D texUv;
uniform sampler2D texRadiance;

uniform vec3 lightPos;
uniform vec3 lightColor;
uniform vec3 giAmbientColor;
uniform float giAmbientIntensity;

#define MAX_DYNAMIC_LIGHTS 64
uniform int dynamicLightCount;
uniform vec3 dynamicLightPosition[MAX_DYNAMIC_LIGHTS];
uniform vec3 dynamicLightDirection[MAX_DYNAMIC_LIGHTS];
uniform vec3 dynamicLightColor[MAX_DYNAMIC_LIGHTS];
uniform float dynamicLightIntensity[MAX_DYNAMIC_LIGHTS];
uniform float dynamicLightRadius[MAX_DYNAMIC_LIGHTS];
uniform float dynamicLightConeCosine[MAX_DYNAMIC_LIGHTS];
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

vec3 fresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

void main()
{
    float specularStrength = 0.5;
    float specularIntensity = 0.5;
    vec4 radianceSamp = mix(GRAY80, texture(texRadiance, matcapTexCoord), useReflections);
    vec4 checkerSamp = mix(WHITE, texture(texUv, fragTexCoord), useCheckerboard);
    vec4 reflectionSamp = texture(texReflection, matcapTexCoord);
    vec3 norm = normalize(fragNormal);

    vec3 lightDir = normalize(lightPos);// light very far away, use direction only. If light has position: normalize(lightPos - fragPos)  
    float diffuse = clamp(dot(norm, lightDir), -1, 1) * 0.3;
    vec3 lighting = giAmbientColor * giAmbientIntensity;
    for (int index = 0; index < dynamicLightCount; ++index)
    {
        vec3 toLight = dynamicLightPosition[index] - fragPos;
        float distanceToLight = length(toLight);
        float radius = max(dynamicLightRadius[index], 0.001);
        if (distanceToLight >= radius)
            continue;
        vec3 directionToLight = toLight / max(distanceToLight, 0.001);
        float attenuation = pow(1.0 - distanceToLight / radius, 2.0);
        float cone = 1.0;
        if (dynamicLightKind[index] == 1)
        {
            float coneDot = dot(normalize(dynamicLightDirection[index]), -directionToLight);
            cone = smoothstep(dynamicLightConeCosine[index], min(1.0, dynamicLightConeCosine[index] + 0.12), coneDot);
        }
        float lambert = max(dot(norm, directionToLight), 0.0);
        lighting += dynamicLightColor[index] * dynamicLightIntensity[index] * attenuation * cone * lambert;
    }

    //phong r dot v ^ strength
    // r - light off normal

    vec3 appliedColor = fragVertColor;

    appliedColor = COLOR_LOOKUP[fragObjectId % NUM_COLOR_LOOKUPS];

    vec3 specR = reflect(lightDir, norm);
    float nDotR = dot(specR, normalize(fragPos));
    vec3 coloredSpec = clamp(pow(nDotR, pow(specularStrength + 1, 8)) * lightColor * specularIntensity, 0, 1);
    vec4 modelSample = texture(texModel, fragTexCoord);
    vec4 baseColor = mix(vec4(appliedColor, 1.0), modelSample, float(hasTexture));
    vec3 combinedLighting = clamp(lighting + vec3(max(diffuse, 0.0)), vec3(0.0), vec3(2.5));
    vec4 beforeMatCap = mix(checkerSamp, baseColor, hasTexture == 1 ? 1.0 : 0.5) * vec4(combinedLighting, 1);
    vec4 beforeReflection = mix(beforeMatCap, radianceSamp * beforeMatCap, 0.75);

    color = mix(beforeReflection, beforeReflection * reflectionSamp, 0.3 * useReflections);
    //color = samp;

}

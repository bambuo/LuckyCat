<template>
  <!-- 招财猫本体（挥手 + 浮动 + 星星），供转场遮罩与页面加载动画共用；尺寸由父级 class 控制 -->
  <svg class="cat-scene" viewBox="0 0 220 220" aria-hidden="true">
    <ellipse cx="110" cy="205" rx="60" ry="8" fill="#E3CFA9" opacity="0.45" />
    <g class="cat-body-group">
      <!-- 尾巴 -->
      <path
        d="M 152 176 C 186 180 200 158 190 132"
        fill="none"
        stroke="#F6E7CC"
        stroke-width="15"
        stroke-linecap="round"
      />
      <!-- 耳朵 -->
      <path d="M 46 20 L 60 64 L 98 42 Z" fill="#FFF9EF" stroke="#EADBBC" stroke-width="3" stroke-linejoin="round" />
      <path d="M 174 20 L 160 64 L 122 42 Z" fill="#FFF9EF" stroke="#EADBBC" stroke-width="3" stroke-linejoin="round" />
      <path d="M 58 31 L 65 56 L 88 45 Z" fill="#F7B3B3" />
      <path d="M 162 31 L 155 56 L 132 45 Z" fill="#F7B3B3" />
      <!-- 身体 -->
      <ellipse cx="110" cy="152" rx="52" ry="46" fill="#FFF9EF" stroke="#EADBBC" stroke-width="3" />
      <!-- 落爪（左右对称） -->
      <ellipse cx="74" cy="182" rx="15" ry="12" fill="#FFF9EF" stroke="#EADBBC" stroke-width="3" />
      <path d="M 69 172 L 69 180 M 79 172 L 79 180" stroke="#E8D8BA" stroke-width="2" stroke-linecap="round" />
      <ellipse cx="146" cy="182" rx="15" ry="12" fill="#FFF9EF" stroke="#EADBBC" stroke-width="3" />
      <path d="M 141 172 L 141 180 M 151 172 L 151 180" stroke="#E8D8BA" stroke-width="2" stroke-linecap="round" />
      <!-- 金币 -->
      <ellipse cx="110" cy="166" rx="25" ry="22" fill="#F5C542" stroke="#D89E1F" stroke-width="2.5" />
      <ellipse cx="110" cy="166" rx="19" ry="16" fill="none" stroke="#E4B32B" stroke-width="2" />
      <text
        x="110"
        y="166"
        text-anchor="middle"
        dominant-baseline="central"
        font-size="24"
        font-weight="700"
        fill="#8A5A00"
        font-family="'PingFang SC','Hiragino Sans GB','Microsoft YaHei',sans-serif"
      >财</text>
      <!-- 头 -->
      <ellipse cx="110" cy="92" rx="63" ry="50" fill="#FFF9EF" stroke="#EADBBC" stroke-width="3" />
      <!-- 脸 -->
      <ellipse cx="87" cy="90" rx="7.5" ry="10" fill="#3F3A33" />
      <ellipse cx="133" cy="90" rx="7.5" ry="10" fill="#3F3A33" />
      <circle cx="84.5" cy="86" r="2.6" fill="#FFFFFF" />
      <circle cx="130.5" cy="86" r="2.6" fill="#FFFFFF" />
      <circle cx="74" cy="104" r="8" fill="#F7B3B3" opacity="0.55" />
      <circle cx="146" cy="104" r="8" fill="#F7B3B3" opacity="0.55" />
      <path d="M 104 101 L 116 101 L 110 108 Z" fill="#F08A8A" stroke="#F08A8A" stroke-width="2" stroke-linejoin="round" />
      <path
        d="M 110 108 Q 104 116 98 110 M 110 108 Q 116 116 122 110"
        fill="none"
        stroke="#C9B08E"
        stroke-width="2.5"
        stroke-linecap="round"
      />
      <path
        d="M 66 94 L 40 88 M 64 104 L 38 106 M 154 94 L 180 88 M 156 104 L 182 106"
        stroke="#E3D2B4"
        stroke-width="2.5"
        stroke-linecap="round"
      />
      <!-- 项圈与铃铛 -->
      <path d="M 58 120 Q 110 146 162 120 L 162 130 Q 110 156 58 130 Z" fill="#E5484D" />
      <circle cx="110" cy="146" r="10" fill="#F5C542" stroke="#D89E1F" stroke-width="2" />
      <path d="M 102 149 L 118 149" stroke="#D89E1F" stroke-width="2" stroke-linecap="round" />
      <circle cx="110" cy="152.5" r="1.8" fill="#8A5A00" />
      <!-- 落下的前爪（搭在金币上） -->
      <path d="M 72 134 L 86 146" stroke="#EADBBC" stroke-width="16" stroke-linecap="round" />
      <path d="M 72 134 L 86 146" stroke="#FFF9EF" stroke-width="12.5" stroke-linecap="round" />
      <ellipse cx="88" cy="148" rx="12.5" ry="11.5" fill="#FFF9EF" stroke="#EADBBC" stroke-width="2.5" />
      <path d="M 83 150 L 83.5 155 M 90 151 L 90.5 156" stroke="#E8D8BA" stroke-width="2" stroke-linecap="round" />
      <!-- 招手的爪 -->
      <g class="paw-group">
        <path d="M 150 140 L 182 98" fill="none" stroke="#FFF9EF" stroke-width="17" stroke-linecap="round" />
        <ellipse cx="184" cy="94" rx="15" ry="13.5" fill="#FFF9EF" stroke="#EADBBC" stroke-width="3" />
        <path d="M 177 85 L 175 91 M 184 83 L 184 90 M 191 85 L 193 91" stroke="#E8D8BA" stroke-width="2" stroke-linecap="round" />
        <path d="M 194 66 Q 202 60 208 68" fill="none" stroke="#F0B90B" stroke-width="3" stroke-linecap="round" opacity="0.85" />
        <path
          class="cat-sparkle"
          d="M 166 60 l 2.5 6 6 2.5 -6 2.5 -2.5 6 -2.5 -6 -6 -2.5 6 -2.5 Z"
          fill="#F0B90B"
          opacity="0.9"
        />
      </g>
    </g>
  </svg>
</template>

<style scoped>
.cat-scene {
  display: block;
  transform-origin: 50% 78%;
  animation: cat-pop 0.55s cubic-bezier(0.34, 1.56, 0.64, 1) both;
}

.cat-body-group {
  animation: cat-bob 1.5s ease-in-out infinite;
}

.paw-group {
  transform-box: view-box;
  transform-origin: 152px 142px;
  animation: paw-wave 0.9s ease-in-out infinite;
}

.cat-sparkle {
  animation: cat-twinkle 1.1s ease-in-out infinite;
}

@keyframes cat-pop {
  from {
    opacity: 0;
    transform: scale(0.7) translateY(14px);
  }
  to {
    opacity: 1;
    transform: scale(1) translateY(0);
  }
}

@keyframes cat-bob {
  0%,
  100% {
    transform: translateY(0);
  }
  50% {
    transform: translateY(-5px);
  }
}

@keyframes paw-wave {
  0%,
  100% {
    transform: rotate(2deg);
  }
  50% {
    transform: rotate(-22deg);
  }
}

@keyframes cat-twinkle {
  0%,
  100% {
    opacity: 0.9;
    transform: scale(1);
  }
  50% {
    opacity: 0.35;
    transform: scale(0.7);
  }
}

@media (prefers-reduced-motion: reduce) {
  .cat-scene,
  .cat-body-group,
  .paw-group,
  .cat-sparkle {
    animation: none;
  }
}
</style>

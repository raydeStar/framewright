import { useId } from 'react'

interface ArtworkProps { variant: number; className?: string; muted?: boolean; label?: string }

export default function Artwork({ variant, className = '', muted = false, label }: ArtworkProps) {
  const id = useId().replaceAll(':', '')
  const scene = ((variant - 1) % 6) + 1
  return (
    <div className={`artwork artwork-${scene} ${muted ? 'artwork-muted' : ''} ${className}`} aria-label={label ?? 'Storyboard frame'} role="img">
      <svg viewBox="0 0 1200 502" preserveAspectRatio="xMidYMid slice">
        <defs>
          <linearGradient id={`${id}-sky`} x1="0" y1="0" x2="1" y2="1"><stop stopColor={scene === 2 ? '#413b34' : '#293738'} /><stop offset=".54" stopColor={scene === 3 ? '#5b5145' : '#6d7770'} /><stop offset="1" stopColor="#a49d88" /></linearGradient>
          <linearGradient id={`${id}-mist`} x1="0" x2="0" y1="0" y2="1"><stop stopColor="#e9ddc3" stopOpacity=".78" /><stop offset="1" stopColor="#a9a58f" stopOpacity="0" /></linearGradient>
          <radialGradient id={`${id}-light`} cx="52%" cy="22%" r="58%"><stop stopColor="#f1d39c" stopOpacity=".72" /><stop offset="1" stopColor="#e4b36c" stopOpacity="0" /></radialGradient>
          <filter id={`${id}-grain`}><feTurbulence type="fractalNoise" baseFrequency=".8" numOctaves="3" seed={variant} /><feColorMatrix values="1 0 0 0 0  0 1 0 0 0  0 0 1 0 0  0 0 0 .12 0" /></filter>
          <filter id={`${id}-soft`}><feGaussianBlur stdDeviation="18" /></filter>
        </defs>
        <rect width="1200" height="502" fill={`url(#${id}-sky)`} />
        <rect width="1200" height="502" fill={`url(#${id}-light)`} />
        <path d="M0 400 Q180 315 340 382 T690 350 T1010 371 T1200 322 V502 H0Z" fill="#202a29" opacity=".82" />
        <path d="M0 438 Q155 380 316 424 T650 397 T944 424 T1200 390 V502 H0Z" fill="#111716" opacity=".92" />
        {scene === 1 && <Aerie />}
        {scene === 2 && <Court />}
        {scene === 3 && <Profile />}
        {scene === 4 && <Guards />}
        {scene === 5 && <Bridge />}
        {scene === 6 && <Remora />}
        <ellipse cx="690" cy="408" rx="590" ry="83" fill={`url(#${id}-mist)`} filter={`url(#${id}-soft)`} opacity=".55" />
        <rect width="1200" height="502" filter={`url(#${id}-grain)`} opacity=".35" />
        <g fill="none" stroke="#f0dfbd" opacity=".42"><path d="M42 40h68M42 40v38M1158 40h-68M1158 40v38M42 462h68M42 462v-38M1158 462h-68M1158 462v-38" /></g>
      </svg>
    </div>
  )
}

function Aerie() { return <>
  <g transform="translate(620 38)">
    <path d="M0 350 82 60l192-54 160 81 66 263Z" fill="#2d3734" stroke="#bcae8b" strokeWidth="3" />
    <path d="m70 282 82-151 150-35 117 74 32 112Z" fill="#677069" opacity=".8" />
    <path d="M128 351V212q0-67 67-67t67 67v139M302 351V205q0-53 53-53t53 53v146" fill="#202826" stroke="#c9b58c" strokeWidth="5" />
    <path d="M-252 233 92 200M-240 253 90 220" stroke="#6d7166" strokeWidth="17" />
    <path d="M-252 233 92 200" stroke="#dbc79c" strokeWidth="2" />
    <path d="m28 35 45 53m297-54-37 61" stroke="#e7c380" strokeWidth="8" opacity=".7" />
  </g>
  <g transform="translate(360 250)" fill="#171d1c"><circle cx="0" cy="-45" r="22" /><path d="m-22-20-22 133h88L20-20Z" /><path d="m-31 15-66 53m107-55 46 68" stroke="#171d1c" strokeWidth="17" /></g>
</> }

function Court() { return <>
  <path d="M134 0v355M331 0 282 350M985 0l60 356" stroke="#bda77f" strokeWidth="18" />
  <path d="M134 0v355M331 0 282 350M985 0l60 356" stroke="#272b27" strokeWidth="9" strokeDasharray="4 17" />
  <path d="M0 355h1200v147H0z" fill="#222724" />
  <path d="m0 390 352-32 247 43 314-64 287 35" fill="none" stroke="#9b8d72" strokeWidth="3" />
  <g transform="translate(515 146)" fill="#151918"><circle cx="0" cy="-30" r="27" /><path d="m-29 0-42 216h144L31 0Z" /><path d="m-12 54-112 77m135-80 98 54" stroke="#151918" strokeWidth="19" /></g>
  <g transform="translate(850 136)" fill="#d6c29d"><circle cx="0" cy="-28" r="30" /><path d="m-28 2-63 221H94L29 2Z" /></g>
</> }

function Profile() { return <>
  <path d="M0 390 283 250l118 252H0z" fill="#212825" />
  <g transform="translate(655 25)">
    <path d="M206 42c-65-17-142 4-177 69-31 57-18 143 40 177l-7 67 61 95 195-12-20-118 62-82-60-46-12-109Z" fill="#bfae8c" />
    <path d="M206 42c-83-20-164 30-186 98l74-22 44 12 24 61 84 38 54-37-12-109Z" fill="#2b302d" />
    <path d="m154 197 70 11-40 27" fill="none" stroke="#343633" strokeWidth="8" />
    <path d="m188 262 50 6" stroke="#4a3d35" strokeWidth="7" />
    <path d="m93 354 48 91m117-105-31 105" stroke="#d1ad67" strokeWidth="18" />
    <circle cx="197" cy="207" r="5" fill="#625640" />
  </g>
</> }

function Guards() { return <>
  <path d="M0 352h1200v150H0z" fill="#202522" />
  {[280, 830].map((x, i) => <g key={x} transform={`translate(${x} 105)`} fill={i ? '#333a36' : '#252d2a'}>
    <circle cx="0" cy="12" r="34" /><path d="m-39 48-42 249h164L42 48Z" />
    <path d="M-73 0v342" stroke="#b8a77f" strokeWidth="12" /><path d="m-93 0 20-45L-52 0Z" fill="#c9b98f" />
  </g>)}
  <g transform="translate(585 200)" fill="#b9a886"><circle cy="-35" r="26" /><path d="m-27-7-48 153H77L29-7Z" /></g>
</> }

function Bridge() { return <>
  <path d="M-20 160 385 92l482 95 370-62" fill="none" stroke="#232b29" strokeWidth="80" />
  <path d="M-20 160 385 92l482 95 370-62" fill="none" stroke="#b9ad8f" strokeWidth="4" />
  <path d="M0 190 386 124l481 95 333-56" fill="none" stroke="#7c8074" strokeWidth="7" strokeDasharray="12 19" />
  <g transform="translate(555 176)" fill="#171d1b"><circle cy="-34" r="25" /><path d="m-27-6-37 182h132L28-6Z" /><path d="M-20 28-88 84M20 31l63 52" stroke="#171d1b" strokeWidth="17" /></g>
</> }

function Remora() { return <>
  <path d="M0 0h470l-78 502H0z" fill="#202725" opacity=".65" />
  <g transform="translate(550 18)">
    <path d="M56 48c89-54 201 8 210 106 6 66-28 120-86 146l14 72 78 130H-15l65-129 7-80C7 261-18 193 6 126Z" fill="#998f7b" />
    <path d="M57 48c68-54 169-19 195 48l-86-20-38 28-28 68-93 18C-7 139 11 84 57 48Z" fill="#262d2b" />
    <path d="m93 189 54 5" stroke="#2f3734" strokeWidth="9" /><circle cx="119" cy="193" r="6" fill="#b47e52" />
    <path d="m144 232 35 12-31 14" fill="none" stroke="#544b40" strokeWidth="6" />
    <path d="m87 224 27 62" stroke="#775247" strokeWidth="5" />
    <path d="M45 377 1 500m188-125 66 127" stroke="#d49758" strokeWidth="15" />
    <path d="m-6 427-96-75" stroke="#998f7b" strokeWidth="29" /><rect x="-128" y="319" width="55" height="82" rx="8" fill="#282f2d" stroke="#c47b53" strokeWidth="4" />
  </g>
</> }


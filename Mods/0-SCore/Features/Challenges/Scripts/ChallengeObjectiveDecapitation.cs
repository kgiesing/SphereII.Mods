using System;
using System.Collections.Generic;
using System.Xml.Linq;
using HarmonyLib;
using Challenges;
using UnityEngine;

namespace Challenges {
    /*
     * A new challenge objective to monitor your Decapitation
     *
     * <!-- Kill two entities by removing their heads. -->
     * <objective type="Decapitation, SCore" count="2" />
     */
    public class ChallengeObjectiveDecapitation : ChallengeObjectiveKillWithItem {
        public override ChallengeObjectiveType ObjectiveType =>
            (ChallengeObjectiveType)ChallengeObjectiveTypeSCore.ChallengeObjectiveDecapitation;

        public string LocalizationKey = "challengeDecapZombies";
        public override string DescriptionText => Localization.Get(LocalizationKey);

        protected override bool Check_EntityKill(DamageResponse dmgResponse, EntityAlive entityDamaged) {
            if (!dmgResponse.Dismember) return false;
            if (entityDamaged.emodel.avatarController is not AvatarZombieController controller) return false;
            if (!controller.headDismembered) return false;
            return base.Check_EntityKill(dmgResponse, entityDamaged);
        }

        public override void ParseElement(XElement e) {
            base.ParseElement(e);
            if (e.HasAttribute("description_key"))
                LocalizationKey = e.GetAttribute("description_key");
        }

    }
}